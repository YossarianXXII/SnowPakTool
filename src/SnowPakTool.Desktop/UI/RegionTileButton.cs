using System.ComponentModel;

namespace SnowPakTool.Desktop;

/// <summary>A keyboard-selectable region card using the same dimensions and header style as truck tiles.</summary>
internal sealed class RegionTileButton : Button, ISearchHighlight, ISearchTextContent
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string RegionName { get; init; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string ReleaseLabel { get; init; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Summary { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool SearchMatch { get; set; }
    bool ISearchHighlight.SearchMatch { get => SearchMatch; set { SearchMatch = value; Invalidate(); } }
    private bool _searchActive;
    bool ISearchHighlight.SearchActive { get => _searchActive; set { _searchActive = value; Invalidate(); } }
    IEnumerable<string?> ISearchTextContent.GetSearchText() => [RegionName, ReleaseLabel, Summary];
    // Cached by RegionTileAssets and shared by every tile for this region.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Image? Thumbnail { get; init; }

    public RegionTileButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        var backgroundColor = _searchActive ? AppColors.Get("SearchActive") : SearchMatch ? AppColors.Get("EngineMatch") : BackColor;
        var textColor = SearchMatch ? SystemColors.ControlText : ForeColor;
        graphics.Clear(backgroundColor);
        var border = Math.Max(1, FlatAppearance.BorderSize);
        var inner = new Rectangle(border, border, Math.Max(1, Width - 2 * border), Math.Max(1, Height - 2 * border));
        var header = new Rectangle(inner.X, inner.Y, inner.Width, 30);
        using var headerFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5f, FontStyle.Bold);
        TextRenderer.DrawText(graphics, RegionName, headerFont, header, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

        var footerHeight = 24 + 14 * Math.Max(1, Summary.Split('\n').Length);
        var picture = new Rectangle(inner.X, header.Bottom, inner.Width, Math.Max(1, inner.Height - 30 - footerHeight));
        using (var background = new SolidBrush(Color.FromArgb(30, 35, 40))) graphics.FillRectangle(background, picture);
        if (Thumbnail is not null)
        {
            var scale = Math.Min((float)picture.Width / Thumbnail.Width, (float)picture.Height / Thumbnail.Height);
            var width = Math.Max(1, (int)Math.Round(Thumbnail.Width * scale));
            var height = Math.Max(1, (int)Math.Round(Thumbnail.Height * scale));
            var destination = new Rectangle(picture.X + (picture.Width - width) / 2, picture.Y + (picture.Height - height) / 2, width, height);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(Thumbnail, destination);
        }

        var release = new Rectangle(inner.X + 2, picture.Bottom + 2, inner.Width - 4, 15);
        using var releaseFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 7.5f, FontStyle.Bold);
        TextRenderer.DrawText(graphics, ReleaseLabel, releaseFont, release, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var summary = new Rectangle(inner.X + 2, release.Bottom, inner.Width - 4, inner.Bottom - release.Bottom);
        using var summaryFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8f);
        TextRenderer.DrawText(graphics, Summary, summaryFont, summary, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        using var pen = new Pen(FlatAppearance.BorderColor, border);
        graphics.DrawRectangle(pen, border / 2f, border / 2f, Width - border, Height - border);
        if (Focused) ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -border - 2, -border - 2), ForeColor, BackColor);
    }
}
