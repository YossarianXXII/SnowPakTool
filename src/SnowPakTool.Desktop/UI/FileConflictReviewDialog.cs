namespace SnowPakTool.Desktop;

internal sealed class FileConflictReviewDialog : Form
{
    internal sealed class Item(string relativePath, string workspacePath, string pakPath)
    {
        public string RelativePath { get; } = relativePath;
        public string WorkspacePath { get; } = workspacePath;
        public string PakPath { get; } = pakPath;
        public string? FinalPath { get; set; }
        public string? SelectedAction { get; set; }
    }

    private readonly string _editDirectory;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        ReadOnly = true,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };

    public List<Item> Items { get; }

    public FileConflictReviewDialog(List<Item> items, string editDirectory)
    {
        Items = items;
        _editDirectory = editDirectory;
        AppColors.Changed += RefreshPaletteColors;
        FormClosed += (_, _) => AppColors.Changed -= RefreshPaletteColors;
        Text = "Review workspace changes";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(950, 500);
        Size = new Size(1250, 700);

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path", HeaderText = "Changed file", FillWeight = 100 });
        _grid.Columns.Add(new DataGridViewButtonColumn { Name = "Pak", HeaderText = "New changed file", Text = "Use PAK", UseColumnTextForButtonValue = true, FillWeight = 22, FlatStyle = FlatStyle.Flat });
        _grid.Columns.Add(new DataGridViewButtonColumn { Name = "Workspace", HeaderText = "Already in workspace", Text = "Keep workspace", UseColumnTextForButtonValue = true, FillWeight = 28, FlatStyle = FlatStyle.Flat });
        _grid.Columns.Add(new DataGridViewButtonColumn { Name = "View", HeaderText = "Inspect", Text = "View changes", UseColumnTextForButtonValue = true, FillWeight = 22, FlatStyle = FlatStyle.Flat });
        foreach (var item in Items)
        {
            var index = _grid.Rows.Add(item.RelativePath, "Use PAK", "Keep workspace", "View changes");
            _grid.Rows[index].Tag = item;
        }
        TableHeaderBehavior.Attach(_grid);
        _grid.CellContentClick += GridCellContentClick;

        var takeAll = new Button { Text = "Use initial.pak", AutoSize = true };
        takeAll.Click += (_, _) =>
        {
            for (var i = 0; i < Items.Count; i++)
            {
                Items[i].FinalPath = CopyToEdits(Items[i].PakPath);
                Items[i].SelectedAction = "Pak";
                var row = _grid.Rows.Cast<DataGridViewRow>().Single(row => ReferenceEquals(row.Tag, Items[i]));
                UpdateRowVisual(row.Index);
            }
        };
        var cancel = new Button { Text = "Cancel unpack", AutoSize = true, DialogResult = DialogResult.Cancel };
        var apply = new Button { Text = "Apply selected versions", AutoSize = true };
        apply.Click += (_, _) =>
        {
            if (Items.Any(item => item.FinalPath is null))
            {
                MessageBox.Show(this, "Choose a version for every changed file, or click Use initial.pak.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK;
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(takeAll);
        buttons.Controls.Add(apply);
        buttons.Controls.Add(cancel);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = $"Review all {Items.Count} files that differ from the workspace. Choose a version or open the change view for editing.", AutoSize = true, Padding = new Padding(0, 0, 0, 8) }, 0, 0);
        layout.Controls.Add(_grid, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        CancelButton = cancel;
    }

    private void GridCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= Items.Count) return;
        var item = (Item)_grid.Rows[e.RowIndex].Tag!;
        switch (_grid.Columns[e.ColumnIndex].Name)
        {
            case "Pak": item.FinalPath = CopyToEdits(item.PakPath); item.SelectedAction = "Pak"; break;
            case "Workspace": item.FinalPath = CopyToEdits(item.WorkspacePath); item.SelectedAction = "Workspace"; break;
            case "View":
                using (var dialog = new FileConflictDialog(item.RelativePath, item.WorkspacePath, item.PakPath))
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        var finalPath = Path.Combine(_editDirectory, $"{Guid.NewGuid():N}.merged");
                        dialog.SaveFinalVersion(finalPath);
                        item.FinalPath = finalPath;
                        item.SelectedAction = "View";
                    }
                }
                break;
            default: return;
        }
        UpdateRowVisual(e.RowIndex);
    }

    private void UpdateRowVisual(int rowIndex)
    {
        foreach (var columnName in new[] { "Pak", "Workspace", "View" })
        {
            var cell = _grid.Rows[rowIndex].Cells[columnName];
            var selected = ((Item)_grid.Rows[rowIndex].Tag!).SelectedAction == columnName;
            cell.Style.BackColor = selected ? AppColors.Get("ConflictSelected") : SystemColors.Control;
            cell.Style.ForeColor = SystemColors.ControlText;
            cell.Style.SelectionBackColor = selected ? AppColors.Get("ConflictSelectedFocus") : SystemColors.Control;
        }
    }

    private void RefreshPaletteColors(object? sender, EventArgs e)
    {
        for (var i = 0; i < Items.Count; i++) UpdateRowVisual(i);
        _grid.Invalidate();
    }

    private string CopyToEdits(string source)
    {
        var destination = Path.Combine(_editDirectory, $"{Guid.NewGuid():N}.merged");
        File.Copy(source, destination);
        return destination;
    }
}
