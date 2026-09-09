using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Rewind
{
    /// <summary>Which hotkey slot fired.</summary>
    internal sealed class HotkeyPressedEventArgs : EventArgs
    {
        public readonly int Slot;

        public HotkeyPressedEventArgs(int slot)
        {
            Slot = slot;
        }
    }

    /// <summary>
    /// An invisible message-only window whose only job is to own the global hotkeys. Windows
    /// posts WM_HOTKEY here whenever a combo is pressed in any app, including a fullscreen game.
    /// Slot 0 is the full clip, slot 1 the short one.
    /// </summary>
    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        public const int Slots = 2;
        private const int WmHotkey = 0x0312;
        private const int FirstHotkeyId = 0x5257; // 'RW'
        private const int ErrorHotkeyAlreadyRegistered = 1409;
        private static readonly IntPtr HwndMessage = new IntPtr(-3);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly bool[] _registered = new bool[Slots];

        public event EventHandler<HotkeyPressedEventArgs> Pressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams { Parent = HwndMessage });
        }

        /// <summary>Claims the combo for a slot. False (with a plain-English reason) if another app owns it.</summary>
        public bool Register(int slot, HotkeySpec spec, out string error)
        {
            if (spec == null) throw new ArgumentNullException("spec");
            if (slot < 0 || slot >= Slots) throw new ArgumentOutOfRangeException("slot");
            Unregister(slot);
            if (!RegisterHotKey(Handle, FirstHotkeyId + slot, spec.Modifiers | HotkeySpec.ModNoRepeat, spec.VirtualKey))
            {
                var code = Marshal.GetLastWin32Error();
                error = code == ErrorHotkeyAlreadyRegistered
                    ? spec.Text + " is already taken by another program."
                    : string.Format("Windows refused the hotkey {0} (error {1}).", spec.Text, code);
                return false;
            }
            _registered[slot] = true;
            error = null;
            return true;
        }

        public void Unregister(int slot)
        {
            if (slot < 0 || slot >= Slots || !_registered[slot]) return;
            UnregisterHotKey(Handle, FirstHotkeyId + slot);
            _registered[slot] = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey)
            {
                var slot = m.WParam.ToInt32() - FirstHotkeyId;
                if (slot >= 0 && slot < Slots)
                {
                    var handler = Pressed;
                    if (handler != null) handler(this, new HotkeyPressedEventArgs(slot));
                }
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            for (var slot = 0; slot < Slots; slot++) Unregister(slot);
            DestroyHandle();
        }
    }
}
