using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Rewind
{
    /// <summary>
    /// The thumbnail grid: one owner-painted control. Tiles share the width (about as many columns
    /// as fit at ~360 px each, every column stretched so the row fills the window), grouped under
    /// day headers, scrolled with the wheel, picked with the mouse or the arrow keys.
    /// </summary>
    internal sealed class ClipGrid : ScrollableControl
    {
        private const int TargetCell = 360;
        private const int Gap = 14;
        private const int HeaderHeight = 30;
        private const int TextHeight = 26;

        private sealed class Tile
        {
            public ClipInfo Clip;
            public Rectangle Bounds;
        }

        private sealed class Header
        {
            public string Text;
            public Rectangle Bounds;
        }

        private readonly Dictionary<string, Image> _thumbs = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Tile> _tiles = new List<Tile>();
        private readonly List<Header> _headers = new List<Header>();
        private readonly Font _headerFont;
        private IList<ClipInfo> _clips = new List<ClipInfo>();
        private int _selected = -1;
        private int _hover = -1;

        public event EventHandler SelectionChanged;
        public event EventHandler ItemActivated;

        /// <summary>Shown in the middle when there are no tiles.</summary>
        public string EmptyText { get; set; }

        public ClipGrid()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AutoScroll = true;
            BackColor = SystemColors.Window;
            TabStop = true;
            EmptyText = "No clips yet.";
            _headerFont = new Font(Font, FontStyle.Bold);
        }

        public int Count { get { return _tiles.Count; } }

        public ClipInfo Selected { get { return _selected >= 0 && _selected < _tiles.Count ? _tiles[_selected].Clip : null; } }

        public bool HasThumbnail(string path)
        {
            return _thumbs.ContainsKey(path);
        }

        /// <summary>Replaces the tiles; keeps the selection on the same file when it is still there.</summary>
        public void SetClips(IList<ClipInfo> clips, string keepSelectedPath)
        {
            if (clips == null) throw new ArgumentNullException("clips");
            _clips = clips;
            Relayout();
            _selected = IndexOf(keepSelectedPath);
            if (_selected < 0 && _tiles.Count > 0) _selected = 0;
            _hover = -1;
            EnsureVisible(_selected);
            Invalidate();
            RaiseSelectionChanged();
        }

        /// <summary>Takes ownership of the image.</summary>
        public void SetThumbnail(string path, Image image)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path");
            if (image == null) throw new ArgumentNullException("image");
            Image old;
            if (_thumbs.TryGetValue(path, out old)) old.Dispose();
            _thumbs[path] = image;
            var index = IndexOf(path);
            if (index >= 0) Invalidate(TileScreenRect(index));
        }

        /// <summary>Swaps in a clip that now knows more (its duration).</summary>
        public void UpdateClip(ClipInfo clip)
        {
            if (clip == null) throw new ArgumentNullException("clip");
            var clips = new List<ClipInfo>(_clips.Count);
            foreach (var existing in _clips) clips.Add(string.Equals(existing.Path, clip.Path, StringComparison.OrdinalIgnoreCase) ? clip : existing);
            _clips = clips.AsReadOnly();
            foreach (var tile in _tiles)
                if (string.Equals(tile.Clip.Path, clip.Path, StringComparison.OrdinalIgnoreCase)) tile.Clip = clip;
            var index = IndexOf(clip.Path);
            if (index >= 0) Invalidate(TileScreenRect(index));
        }

        public bool Select(string path)
        {
            var index = IndexOf(path);
            if (index < 0) return false;
            SetSelected(index);
            return true;
        }

        public static string DayLabel(DateTime taken)
        {
            var today = DateTime.Today;
            if (taken.Date == today) return "Today";
            if (taken.Date == today.AddDays(-1)) return "Yesterday";
            return taken.ToString(taken.Year == today.Year ? "dddd d MMMM" : "dddd d MMMM yyyy");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var image in _thumbs.Values) image.Dispose();
                _thumbs.Clear();
                _headerFont.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- layout ----

        private void Relayout()
        {
            _tiles.Clear();
            _headers.Clear();
            var width = ClientSize.Width;
            var usable = Math.Max(200, width - Gap * 2);
            var columns = Math.Max(1, (int)Math.Round(usable / (double)(TargetCell + Gap)));
            var cell = (usable - Gap * (columns - 1)) / columns;
            var thumbHeight = cell * 9 / 16;
            var cellHeight = thumbHeight + TextHeight;

            var y = Gap;
            var column = 0;
            string day = null;
            foreach (var clip in _clips)
            {
                var label = DayLabel(clip.Taken);
                if (label != day)
                {
                    if (column > 0)
                    {
                        y += cellHeight + Gap;
                        column = 0;
                    }
                    _headers.Add(new Header { Text = label, Bounds = new Rectangle(Gap, y, usable, HeaderHeight) });
                    y += HeaderHeight;
                    day = label;
                }
                _tiles.Add(new Tile { Clip = clip, Bounds = new Rectangle(Gap + column * (cell + Gap), y, cell, cellHeight) });
                column++;
                if (column == columns)
                {
                    column = 0;
                    y += cellHeight + Gap;
                }
            }
            if (column > 0) y += cellHeight + Gap;
            AutoScrollMinSize = new Size(0, y);
        }

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            Relayout();
            Invalidate();
        }

        private int IndexOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return -1;
            for (var i = 0; i < _tiles.Count; i++)
                if (string.Equals(_tiles[i].Clip.Path, path, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private Rectangle TileScreenRect(int index)
        {
            var bounds = _tiles[index].Bounds;
            bounds.Offset(0, AutoScrollPosition.Y);
            bounds.Inflate(4, 4);
            return bounds;
        }

        private void EnsureVisible(int index)
        {
            if (index < 0 || index >= _tiles.Count) return;
            var bounds = _tiles[index].Bounds;
            var top = -AutoScrollPosition.Y;
            var height = ClientSize.Height;
            if (bounds.Top - Gap < top) AutoScrollPosition = new Point(0, Math.Max(0, bounds.Top - Gap - HeaderHeight));
            else if (bounds.Bottom + Gap > top + height) AutoScrollPosition = new Point(0, bounds.Bottom + Gap - height);
        }

        // ---- painting ----

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            if (_tiles.Count == 0)
            {
                TextRenderer.DrawText(g, EmptyText, Font, ClientRectangle, SystemColors.GrayText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            var offset = AutoScrollPosition.Y;
            g.TranslateTransform(0, offset);
            var visible = new Rectangle(0, -offset, ClientSize.Width, ClientSize.Height);
            foreach (var header in _headers)
                if (header.Bounds.IntersectsWith(visible)) DrawHeader(g, header);
            for (var i = 0; i < _tiles.Count; i++)
                if (_tiles[i].Bounds.IntersectsWith(visible)) DrawTile(g, _tiles[i], i == _selected, i == _hover);
        }

        private void DrawHeader(Graphics g, Header header)
        {
            var textRect = new Rectangle(header.Bounds.X, header.Bounds.Y, header.Bounds.Width, header.Bounds.Height);
            TextRenderer.DrawText(g, header.Text, _headerFont, textRect, SystemColors.GrayText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            var textWidth = TextRenderer.MeasureText(g, header.Text, _headerFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            using (var pen = new Pen(SystemColors.ControlLight))
            {
                var y = header.Bounds.Y + header.Bounds.Height / 2;
                g.DrawLine(pen, header.Bounds.X + textWidth + 10, y, header.Bounds.Right, y);
            }
        }

        private void DrawTile(Graphics g, Tile tile, bool selected, bool hover)
        {
            var thumb = new Rectangle(tile.Bounds.X, tile.Bounds.Y, tile.Bounds.Width, tile.Bounds.Width * 9 / 16);
            Image image;
            if (_thumbs.TryGetValue(tile.Clip.Path, out image))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(image, thumb);
            }
            else
            {
                using (var brush = new SolidBrush(Color.FromArgb(48, 48, 52))) g.FillRectangle(brush, thumb);
                var cx = thumb.X + thumb.Width / 2;
                var cy = thumb.Y + thumb.Height / 2;
                var r = Math.Max(10, thumb.Height / 6);
                var oldMode = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(Color.FromArgb(120, 120, 128)))
                    g.FillPolygon(brush, new[] { new Point(cx - r / 2, cy - r), new Point(cx + r, cy), new Point(cx - r / 2, cy + r) });
                g.SmoothingMode = oldMode;
            }

            if (selected)
            {
                using (var pen = new Pen(SystemColors.Highlight, 3))
                {
                    var frame = thumb;
                    frame.Inflate(2, 2);
                    g.DrawRectangle(pen, frame);
                }
            }
            else if (hover)
            {
                using (var pen = new Pen(SystemColors.ControlDark))
                {
                    var frame = thumb;
                    frame.Inflate(1, 1);
                    g.DrawRectangle(pen, frame);
                }
            }

            var clip = tile.Clip;
            var textRect = new Rectangle(thumb.X, thumb.Bottom + 3, thumb.Width, TextHeight - 3);
            var title = clip.Title;
            var titleWidth = TextRenderer.MeasureText(g, title, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, title, Font, textRect, selected ? SystemColors.Highlight : SystemColors.ControlText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            var timeRect = new Rectangle(textRect.X + titleWidth + 8, textRect.Y, Math.Max(0, textRect.Width - titleWidth - 8), textRect.Height);
            TextRenderer.DrawText(g, clip.Taken.ToString("HH:mm"), Font, timeRect, SystemColors.GrayText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (clip.Duration.HasValue)
                TextRenderer.DrawText(g, ClipsForm.Length(clip.Duration.Value), Font, textRect, SystemColors.GrayText,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // ---- mouse ----

        private int HitTest(Point location)
        {
            var point = new Point(location.X, location.Y - AutoScrollPosition.Y);
            for (var i = 0; i < _tiles.Count; i++)
                if (_tiles[i].Bounds.Contains(point)) return i;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            var index = HitTest(e.Location);
            if (index >= 0) SetSelected(index);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && HitTest(e.Location) >= 0) RaiseActivated();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var index = HitTest(e.Location);
            if (index == _hover) return;
            var old = _hover;
            _hover = index;
            if (old >= 0 && old < _tiles.Count) Invalidate(TileScreenRect(old));
            if (index >= 0) Invalidate(TileScreenRect(index));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover < 0) return;
            var old = _hover;
            _hover = -1;
            if (old < _tiles.Count) Invalidate(TileScreenRect(old));
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            var form = FindForm();
            if (form != null && form == Form.ActiveForm && !Focused) Focus(); // so the wheel scrolls the grid, not whatever had focus
        }

        // ---- keyboard ----

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                case Keys.Home:
                case Keys.End:
                case Keys.Enter:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || _tiles.Count == 0) return;
            var current = _selected < 0 ? 0 : _selected;
            var next = -1;
            switch (e.KeyCode)
            {
                case Keys.Left: next = Math.Max(0, current - 1); break;
                case Keys.Right: next = Math.Min(_tiles.Count - 1, current + 1); break;
                case Keys.Up: next = Neighbour(current, -1); break;
                case Keys.Down: next = Neighbour(current, 1); break;
                case Keys.Home: next = 0; break;
                case Keys.End: next = _tiles.Count - 1; break;
                case Keys.Enter:
                    RaiseActivated();
                    e.Handled = true;
                    return;
            }
            if (next < 0) return;
            SetSelected(next);
            e.Handled = true;
        }

        /// <summary>The nearest tile in the row above (-1) or below (+1), by horizontal distance.</summary>
        private int Neighbour(int index, int direction)
        {
            var from = _tiles[index].Bounds;
            var best = -1;
            var bestRow = direction < 0 ? int.MinValue : int.MaxValue;
            var bestDistance = int.MaxValue;
            for (var i = 0; i < _tiles.Count; i++)
            {
                var bounds = _tiles[i].Bounds;
                if (direction < 0 ? bounds.Y >= from.Y : bounds.Y <= from.Y) continue;
                var closerRow = direction < 0 ? bounds.Y > bestRow : bounds.Y < bestRow;
                var distance = Math.Abs(bounds.X - from.X);
                if (closerRow || (bounds.Y == bestRow && distance < bestDistance))
                {
                    best = i;
                    bestRow = bounds.Y;
                    bestDistance = distance;
                }
            }
            return best < 0 ? index : best;
        }

        private void SetSelected(int index)
        {
            if (index == _selected) return;
            var old = _selected;
            _selected = index;
            if (old >= 0 && old < _tiles.Count) Invalidate(TileScreenRect(old));
            if (index >= 0 && index < _tiles.Count) Invalidate(TileScreenRect(index));
            EnsureVisible(index);
            RaiseSelectionChanged();
        }

        private void RaiseSelectionChanged()
        {
            var handler = SelectionChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseActivated()
        {
            var handler = ItemActivated;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }
}
