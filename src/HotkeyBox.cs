using System;
using System.Windows.Forms;

namespace Rewind
{
    /// <summary>
    /// A text box you set by pressing the combo, like Medal's: hold Ctrl+Alt and it shows
    /// "ctrl+alt+", press P and it becomes "ctrl+alt+p". Backspace or Delete turns an optional
    /// key off. Tab still moves on. Typing letters no longer spells a hotkey out; the text is
    /// always something HotkeySpec.Parse reads back.
    /// </summary>
    internal sealed class HotkeyBox : TextBox
    {
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyUp = 0x0105;

        private readonly bool _optional;
        /// <summary>The value before modifiers started going down; put back if they're let go without a key.</summary>
        private string _before;

        public HotkeyBox(bool optional)
        {
            _optional = optional;
            ShortcutsEnabled = false;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            var modifiers = keyData & Keys.Modifiers;

            if (key == Keys.Tab && (modifiers & (Keys.Control | Keys.Alt)) == 0) return base.ProcessCmdKey(ref msg, keyData);

            if (modifiers == Keys.None && (key == Keys.Back || key == Keys.Delete))
            {
                if (_optional) SetCombo("off");
                _before = null;
                return true;
            }

            var combo = HotkeySpec.FromKeys(keyData);
            if (combo == null)
            {
                if (_before == null) _before = Text;
                SetCombo(HotkeySpec.Held(keyData));
            }
            else
            {
                SetCombo(combo);
                _before = null;
            }
            // Handled here, so no WM_CHAR follows and Alt+letter never reaches the window's menu.
            return true;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmKeyUp || m.Msg == WmSysKeyUp)
            {
                // All modifiers let go without a real key: put the old value back.
                if (_before != null && (ModifierKeys & (Keys.Control | Keys.Alt | Keys.Shift)) == 0)
                {
                    SetCombo(_before);
                    _before = null;
                }
                // Swallow Alt's key-up, or Windows opens the window menu and eats the next key.
                if (m.Msg == WmSysKeyUp) return;
            }
            base.WndProc(ref m);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            if (_before != null) { SetCombo(_before); _before = null; }
            base.OnLostFocus(e);
        }

        private void SetCombo(string text)
        {
            Text = text;
            SelectionStart = text.Length;
        }
    }
}
