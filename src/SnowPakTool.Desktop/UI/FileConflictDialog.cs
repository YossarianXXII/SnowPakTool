using System.Text;

namespace SnowPakTool.Desktop;

internal sealed class FileConflictDialog : Form
{
    private readonly RichTextBox _workspaceText = CreateTextPane(readOnly: true);
    private readonly RichTextBox _finalText = CreateTextPane(readOnly: false);
    private readonly RichTextBox _pakText = CreateTextPane(readOnly: true);
    private readonly Button _saveButton = new() { Text = "Use final version", AutoSize = true, Enabled = false };
    private readonly bool _isTextConflict;
    private string? _selectedBinarySource;
    private Encoding _finalEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private string _finalNewline = "\n";

    public FileConflictDialog(string relativePath, string existingPath, string incomingPath)
    {
        Text = "Resolve file difference";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1050, 600);
        Size = new Size(1500, 850);

        var info = new Label
        {
            Text = $"Different versions: {relativePath}{Environment.NewLine}" +
                   $"Workspace: {new FileInfo(existingPath).Length:N0} bytes    " +
                   $"Incoming PAK: {new FileInfo(incomingPath).Length:N0} bytes",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 0, 0, 8)
        };

        var leftHeader = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        leftHeader.Controls.Add(new Label { Text = "Workspace version", AutoSize = true, Padding = new Padding(0, 7, 6, 0) });
        var useWorkspace = new Button { Text = "Use workspace →", AutoSize = true };
        leftHeader.Controls.Add(useWorkspace);

        var middleHeader = new Label { Text = "Final version (editable)", AutoSize = true, Padding = new Padding(0, 7, 0, 0) };

        var rightHeader = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        var usePak = new Button { Text = "← Use PAK", AutoSize = true };
        rightHeader.Controls.Add(usePak);
        rightHeader.Controls.Add(new Label { Text = "Incoming PAK version", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });

        var panes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
        panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        panes.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panes.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panes.Controls.Add(leftHeader, 0, 0);
        panes.Controls.Add(middleHeader, 1, 0);
        panes.Controls.Add(rightHeader, 2, 0);

        var existingText = string.Empty;
        var incomingText = string.Empty;
        Encoding existingEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _isTextConflict = TryReadText(existingPath, out existingText, out existingEncoding) &&
                          TryReadText(incomingPath, out incomingText, out _);
        if (_isTextConflict)
        {
            _workspaceText.Text = existingText;
            _pakText.Text = incomingText;
            HighlightDiff(existingText, incomingText);
            _finalText.Text = "Choose a source version to start editing.";
            _finalText.ForeColor = SystemColors.GrayText;
            _finalText.ReadOnly = true;
            useWorkspace.Click += (_, _) => SelectTextVersion(existingText, existingEncoding);
            usePak.Click += (_, _) => SelectTextVersion(incomingText, ReadEncoding(incomingPath));
        }
        else
        {
            var difference = FindFirstDifference(existingPath, incomingPath);
            _workspaceText.Text = BuildBinaryPreview(existingPath, difference);
            _pakText.Text = BuildBinaryPreview(incomingPath, difference);
            _finalText.Text = "This is a binary file. Choose which original file to keep; text editing is unavailable.";
            _finalText.ReadOnly = true;
            useWorkspace.Click += (_, _) => SelectBinaryVersion(existingPath);
            usePak.Click += (_, _) => SelectBinaryVersion(incomingPath);
        }
        AppColors.Changed += RefreshPaletteColors;
        FormClosed += (_, _) => AppColors.Changed -= RefreshPaletteColors;

        panes.Controls.Add(_workspaceText, 0, 1);
        panes.Controls.Add(_finalText, 1, 1);
        panes.Controls.Add(_pakText, 2, 1);

        var cancelButton = new Button { Text = "Cancel unpack", AutoSize = true, DialogResult = DialogResult.Cancel };
        _saveButton.Click += (_, _) => DialogResult = DialogResult.OK;
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0)
        };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(_saveButton);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(info, 0, 0);
        layout.Controls.Add(panes, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);

        CancelButton = cancelButton;
        AcceptButton = _saveButton;
    }

    public void SaveFinalVersion(string path)
    {
        if (_isTextConflict)
        {
            using var writer = new StreamWriter(path, append: false, _finalEncoding);
            writer.Write(NormalizeNewlines(_finalText.Text, _finalNewline));
        }
        else
        {
            File.Copy(_selectedBinarySource!, path, overwrite: false);
        }
    }

    private void SelectTextVersion(string text, Encoding encoding)
    {
        _selectedBinarySource = null;
        _finalEncoding = encoding;
        _finalNewline = DetectNewline(text);
        _finalText.ReadOnly = false;
        _finalText.ForeColor = SystemColors.WindowText;
        _finalText.Text = text;
        _finalText.SelectionStart = 0;
        _finalText.SelectionLength = 0;
        _finalText.Focus();
        _saveButton.Enabled = true;
    }

    private void SelectBinaryVersion(string path)
    {
        _selectedBinarySource = path;
        _finalText.Text = "Selected binary version from: " + path + Environment.NewLine +
                          "Binary files cannot be edited in this text area.";
        _saveButton.Enabled = true;
    }

    private void HighlightDiff(string workspaceText, string pakText)
    {
        var oldLines = workspaceText.Split('\n');
        var newLines = pakText.Split('\n');
        var oldChanged = new bool[oldLines.Length];
        var newChanged = new bool[newLines.Length];
        var cells = (long)(oldLines.Length + 1) * (newLines.Length + 1);
        if (cells <= 4_000_000)
        {
            var width = newLines.Length + 1;
            var lcs = new int[(int)cells];
            for (var i = oldLines.Length - 1; i >= 0; i--)
                for (var j = newLines.Length - 1; j >= 0; j--)
                    lcs[i * width + j] = oldLines[i] == newLines[j]
                        ? lcs[(i + 1) * width + j + 1] + 1
                        : Math.Max(lcs[(i + 1) * width + j], lcs[i * width + j + 1]);

            var oldIndex = 0;
            var newIndex = 0;
            while (oldIndex < oldLines.Length && newIndex < newLines.Length)
            {
                if (oldLines[oldIndex] == newLines[newIndex]) { oldIndex++; newIndex++; }
                else if (lcs[(oldIndex + 1) * width + newIndex] >= lcs[oldIndex * width + newIndex + 1]) oldChanged[oldIndex++] = true;
                else newChanged[newIndex++] = true;
            }
            while (oldIndex < oldLines.Length) oldChanged[oldIndex++] = true;
            while (newIndex < newLines.Length) newChanged[newIndex++] = true;
        }
        else
        {
            var prefix = 0;
            while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix]) prefix++;
            var oldSuffix = oldLines.Length - 1;
            var newSuffix = newLines.Length - 1;
            while (oldSuffix >= prefix && newSuffix >= prefix && oldLines[oldSuffix] == newLines[newSuffix]) { oldSuffix--; newSuffix--; }
            for (var i = prefix; i <= oldSuffix; i++) oldChanged[i] = true;
            for (var i = prefix; i <= newSuffix; i++) newChanged[i] = true;
        }

        ApplyLineHighlights(_workspaceText, oldLines, oldChanged, AppColors.Get("ConflictOldText"));
        ApplyLineHighlights(_pakText, newLines, newChanged, AppColors.Get("ConflictNewText"));
    }

    private static void ApplyLineHighlights(RichTextBox pane, string[] lines, bool[] changed, Color color)
    {
        var offset = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var length = lines[i].Length + (i < lines.Length - 1 ? 1 : 0);
            if (changed[i] && length > 0)
            {
                pane.Select(offset, length);
                pane.SelectionBackColor = color;
            }
            offset += length;
        }
        pane.Select(0, 0);
    }

    private void RefreshPaletteColors(object? sender, EventArgs e)
    {
        if (_isTextConflict) HighlightDiff(_workspaceText.Text, _pakText.Text);
    }

    private static RichTextBox CreateTextPane(bool readOnly) => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = readOnly,
        WordWrap = false,
        DetectUrls = false,
        HideSelection = false,
        Font = new Font("Consolas", 9),
        ScrollBars = RichTextBoxScrollBars.Both
    };

    private static bool TryReadText(string path, out string text, out Encoding encoding)
    {
        var sampleLength = (int)Math.Min(new FileInfo(path).Length, 8192);
        var sample = new byte[sampleLength];
        using (var stream = File.OpenRead(path)) stream.ReadExactly(sample);
        if (!LooksLikeText(sample))
        {
            text = string.Empty;
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            return false;
        }

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        text = reader.ReadToEnd();
        encoding = DetectEncoding(path);
        return true;
    }

    private static Encoding ReadEncoding(string path)
    {
        return DetectEncoding(path);
    }

    private static Encoding DetectEncoding(string path)
    {
        var bom = new byte[4];
        int read;
        using (var stream = File.OpenRead(path)) read = stream.Read(bom, 0, bom.Length);
        if (read >= 4 && bom[0] == 0x00 && bom[1] == 0x00 && bom[2] == 0xFE && bom[3] == 0xFF) return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        if (read >= 4 && bom[0] == 0xFF && bom[1] == 0xFE && bom[2] == 0x00 && bom[3] == 0x00) return new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private static string DetectNewline(string text)
    {
        var firstNewline = text.IndexOf('\n');
        return firstNewline > 0 && text[firstNewline - 1] == '\r' ? "\r\n" : "\n";
    }

    private static string NormalizeNewlines(string text, string newline) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", newline);

    private static bool LooksLikeText(byte[] sample)
    {
        if (sample.Length == 0 || sample.Contains((byte)0)) return sample.Length == 0;
        var controls = sample.Count(value => value < 32 && value != 9 && value != 10 && value != 12 && value != 13);
        return controls * 100 / sample.Length < 2;
    }

    private static long FindFirstDifference(string firstPath, string secondPath)
    {
        using var first = File.OpenRead(firstPath);
        using var second = File.OpenRead(secondPath);
        var firstBuffer = new byte[64 * 1024];
        var secondBuffer = new byte[firstBuffer.Length];
        long offset = 0;
        while (true)
        {
            var firstRead = first.Read(firstBuffer, 0, firstBuffer.Length);
            var secondRead = second.Read(secondBuffer, 0, secondBuffer.Length);
            var commonLength = Math.Min(firstRead, secondRead);
            for (var i = 0; i < commonLength; i++)
                if (firstBuffer[i] != secondBuffer[i]) return offset + i;
            if (firstRead != secondRead) return offset + commonLength;
            if (firstRead == 0) return offset;
            offset += firstRead;
        }
    }

    private static string BuildBinaryPreview(string path, long differenceOffset)
    {
        var length = new FileInfo(path).Length;
        var start = Math.Max(0, differenceOffset - 32);
        var count = (int)Math.Min(64, length - start);
        var bytes = new byte[count];
        using (var stream = File.OpenRead(path))
        {
            stream.Position = start;
            stream.ReadExactly(bytes);
        }
        return $"[Binary preview near byte {start:N0}]{Environment.NewLine}" +
               string.Join(" ", bytes.Select(value => value.ToString("X2")));
    }
}
