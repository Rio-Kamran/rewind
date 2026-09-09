using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Rewind
{
    /// <summary>
    /// An invisible message-only window whose only job is to own the global hotkey. Windows
    /// posts WM_HOTKEY here whenever the combo is pressed in any app, including a fullscreen game.
    /// </summary>
    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int WmHotkey = 0x0312;
        private const int HotkeyId = 0x5257; // 'RW'
        private const int ErrorHotkeyAlreadyRegistered = 1409;
        private static readonly IntPtr HwndMessage = new IntPtr(-3);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private bool _registered;

        public event EventHandler Pressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams { Parent = HwndMessage });
        }

        /// <summary>Claims the combo. False (with a plain-English reason) if another app owns it.</summary>
        public bool Register(HotkeySpec spec, out string error)
        {
            if (spec == null) throw new ArgumentNullException("spec");
            Unregister();
            if (!RegisterHotKey(Handle, HotkeyId, spec.Modifiers | HotkeySpec.ModNoRepeat, spec.VirtualKey))
            {
                var code = Marshal.GetLastWin32Error();
                error = code == ErrorHotkeyAlreadyRegistered
                    ? spec.Text + " is already taken by another program."
                    : string.Format("Windows refused the hotkey {0} (error {1}).", spec.Text, code);
                return false;
            }
            _registered = true;
            error = null;
            return true;
        }

        public void Unregister()
        {
            if (!_registered) return;
            UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
            {
                var handler = Pressed;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            DestroyHandle();
        }
    }
}
