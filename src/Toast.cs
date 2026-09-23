using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Rewind
{
    /// <summary>
    /// The on-screen card Medal shows in the corner: "Clip saved". Windows hides tray balloons
    /// while a game is fullscreen, so this is a tiny always-on-top window that never takes focus
    /// (WS_EX_NOACTIVATE), drawn on the primary monitor's top-right, gone after three seconds.
    /// Shows over borderless and windowed games; exclusive fullscreen can't show any window.
    /// One instance flashes messages; a second, pinned one stays put while a long recording runs.
    /// </summary>
    internal sealed class Toast : Form
    {
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExTopmost = 0x00000008;
        private const int EdgeGap = 24;
        private const int LifeMs = 3000;
        private static readonly Size CardSize = new Size(340, 64);
        private static Toast _flash, _pin;

        private readonly bool _pinned;
        private readonly System.Windows.Forms.Timer _life = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _fade = new System.Windows.Forms.Timer { Interval = 30 };
        private readonly Font _titleFont, _detailFont;
        private string _title = "", _detail = "";
        private Color _dot = Color.FromArgb(230, 40, 40);
        private Action _onClick;

        /// <summary>A message for three seconds; click runs onClick (or nothing). UI thread only.</summary>
        public static void Flash(string title, string detail, Action onClick)
        {
            if (_flash == null || _flash.IsDisposed) _flash = new Toast(false);
            _flash.Present(title, detail, onClick);
        }

        /// <summary>A card that stays until Unpin(): the "recording" pill. Calling it again just updates the text.</summary>
        public static void Pin(string title, string detail)
        {
            if (_pin == null || _pin.IsDisposed) _pin = new Toast(true);
            _pin.Present(title, detail, null);
        }

        public static void Unpin()
        {
            if (_pin != null && !_pin.IsDisposed) _pin.Hide();
        }

        public static void CloseAll()
        {
            if (_flash != null && !_flash.IsDisposed) _flash.Dispose();
            if (_pin != null && !_pin.IsDisposed) _pin.Dispose();
            _flash = _pin = null;
        }

        private Toast(bool pinned)
        {
            _pinned = pinned;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = CardSize;
            BackColor = Color.FromArgb(30, 30, 34);
            Opacity = 0.96;
            Cursor = pinned ? Cursors.Default : Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            _titleFont = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            _detailFont = new Font("Segoe UI", 9f);
            _life.Tick += (s, e) => { _life.Stop(); _fade.Start(); };
            _fade.Tick += (s, e) => Fade();
            Click += (s, e) => { var action = _onClick; Hide(); if (action != null) action(); };
            using (var path = RoundedRect(new Rectangle(0, 0, Width, Height), 12)) Region = new Region(path);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTopmost;
                return cp;
            }
        }

        private void Present(string title, string detail, Action onClick)
        {
            _title = title ?? "";
            _detail = detail ?? "";
            _onClick = onClick;
            _dot = _title.StartsWith("Not ", StringComparison.OrdinalIgnoreCase) || _title.IndexOf("NOT", StringComparison.Ordinal) >= 0
                ? Color.FromArgb(235, 160, 30) : Color.FromArgb(230, 40, 40);
            _fade.Stop();
            _life.Stop();
            Opacity = 0.96;
            Place();
            if (!Visible) Show();
            Invalidate();
            if (!_pinned)
            {
                _life.Interval = LifeMs;
                _life.Start();
            }
        }

        /// <summary>Top-right of the main monitor; a flashed card sits under the pinned one when both are up.</summary>
        private void Place()
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            var below = !_pinned && _pin != null && !_pin.IsDisposed && _pin.Visible ? CardSize.Height + 10 : 0;
            Location = new Point(area.Right - Width - EdgeGap, area.Top + EdgeGap + below);
        }

        private void Fade()
        {
            var next = Opacity - 0.08;
            if (next <= 0.05)
            {
                _fade.Stop();
                Hide();
                Opacity = 0.96;
                return;
            }
            Opacity = next;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(_dot)) g.FillEllipse(brush, 16, Height / 2 - 7, 14, 14);
            var textLeft = 40;
            var rect = new Rectangle(textLeft, 11, Width - textLeft - 12, 22);
            TextRenderer.DrawText(g, _title, _titleFont, rect, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            var detailRect = new Rectangle(textLeft, 34, Width - textLeft - 12, 20);
            TextRenderer.DrawText(g, _detail, _detailFont, detailRect, Color.FromArgb(190, 190, 200), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _life.Dispose();
                _fade.Dispose();
                _titleFont.Dispose();
                _detailFont.Dispose();
            }
            base.Dispose(disposing);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
