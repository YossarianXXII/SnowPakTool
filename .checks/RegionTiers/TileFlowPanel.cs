namespace SnowPakTool.Desktop;

using System.Windows.Forms;

/// <summary>Tile flow panel with the insertion marker used during truck reordering.</summary>
internal sealed class TileFlowPanel : FlowLayoutPanel
    {
        private readonly bool _keepVerticalScrollbar;
        private Panel? _movingTile;

        public TileFlowPanel(bool keepVerticalScrollbar)
        {
            _keepVerticalScrollbar = keepVerticalScrollbar;
            DoubleBuffered = true;
        }

        public int DropIndex { get; private set; } = -1;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                if (_keepVerticalScrollbar)
                    parameters.Style |= 0x00200000; // WS_VSCROLL keeps the bar visible when the range is empty.
                return parameters;
            }
        }

        public (int Index, bool AtPreviousRowEnd) GetInsertionPosition(Point point, Panel? movingTile = null)
        {
            var tiles = Controls.Cast<Control>().Where(tile => !ReferenceEquals(tile, movingTile)).ToArray();
            if (tiles.Length == 0)
                return (0, false);

            var rows = tiles
                .Select((tile, index) => (Tile: tile, Index: index))
                .GroupBy(item => item.Tile.Top)
                .OrderBy(row => row.Key)
                .Select(row => row.OrderBy(item => item.Tile.Left).ToArray())
                .ToArray();
            var row = rows
                .OrderBy(items => VerticalDistance(point.Y, items[0].Tile.Top, items[0].Tile.Bottom))
                .First();

            foreach (var item in row)
            {
                if (point.X < item.Tile.Left + item.Tile.Width / 2)
                    return (item.Index, false);
            }

            var indexAfterRow = row[^1].Index + 1;
            var isBetweenRows = row[^1].Tile.Top != rows[^1][^1].Tile.Top && indexAfterRow < tiles.Length;
            return (indexAfterRow, isBetweenRows);
        }

        private static int VerticalDistance(int pointY, int top, int bottom)
        {
            if (pointY < top)
                return top - pointY;
            return pointY > bottom ? pointY - bottom : 0;
        }

        public void SetDropPosition(int index, Panel? movingTile, bool atPreviousRowEnd)
        {
            var count = Controls.Cast<Control>().Count(tile => !ReferenceEquals(tile, movingTile));
            index = Math.Clamp(index, 0, count);
            if (DropIndex == index && _atPreviousRowEnd == atPreviousRowEnd && ReferenceEquals(_movingTile, movingTile))
                return;
            DropIndex = index;
            _movingTile = movingTile;
            _atPreviousRowEnd = atPreviousRowEnd;
            Invalidate();
        }

        public void ClearDropIndex()
        {
            if (DropIndex < 0)
                return;
            DropIndex = -1;
            _movingTile = null;
            _atPreviousRowEnd = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (DropIndex < 0)
                return;

            Rectangle marker;
            var tiles = Controls.Cast<Control>().Where(tile => !ReferenceEquals(tile, _movingTile)).ToArray();
            if (tiles.Length == 0)
            {
                marker = new Rectangle(Padding.Left + 2, Padding.Top + 2, 2, 210);
            }
            else if (DropIndex < tiles.Length && _atPreviousRowEnd && DropIndex > 0 &&
                     tiles[DropIndex - 1].Top != tiles[DropIndex].Top)
            {
                var previous = tiles[DropIndex - 1];
                marker = new Rectangle(previous.Right + previous.Margin.Right - 1, previous.Top, 2, previous.Height);
            }
            else if (DropIndex < tiles.Length)
            {
                var tile = tiles[DropIndex];
                marker = new Rectangle(tile.Left - tile.Margin.Left - 1, tile.Top, 2, tile.Height);
            }
            else
            {
                var tile = tiles[^1];
                marker = new Rectangle(tile.Right + tile.Margin.Right - 1, tile.Top, 2, tile.Height);
            }

            using var pen = new Pen(Color.Black, 2);
            e.Graphics.DrawLine(pen, marker.Left, marker.Top, marker.Left, marker.Bottom);
        }

        private bool _atPreviousRowEnd;
    }
