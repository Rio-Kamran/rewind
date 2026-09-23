using System;
using System.Windows.Forms;

namespace Rewind
{
    /// <summary>
    /// A parsed hotkey like "ctrl+alt+p" or "F9", in the form Windows' RegisterHotKey wants:
    /// a modifier bitmask plus a virtual-key code. Immutable.
    /// </summary>
    internal sealed class HotkeySpec
    {
        public const uint ModAlt = 0x0001;
        public const uint ModControl = 0x0002;
        public const uint ModShift = 0x0004;
        public const uint ModWin = 0x0008;
        /// <summary>Holding the key down fires once, not once per key-repeat.</summary>
        public const uint ModNoRepeat = 0x4000;

        public readonly uint Modifiers;
        public readonly uint VirtualKey;
        /// <summary>Normalised text, e.g. "Ctrl+Alt+P", for messages and the tray menu.</summary>
        public readonly string Text;

        private HotkeySpec(uint modifiers, uint virtualKey, string text)
        {
            Modifiers = modifiers;
            VirtualKey = virtualKey;
            Text = text;
        }

        public static HotkeySpec Parse(string text)
        {
            if (text == null || text.Trim().Length == 0)
                throw new ConfigException("hotkey is empty. Example: hotkey=ctrl+alt+p");

            var parts = text.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            uint modifiers = 0;
            var key = Keys.None;

            foreach (var raw in parts)
            {
                var part = raw.Trim().ToLowerInvariant();
                switch (part)
                {
                    case "ctrl":
                    case "control": modifiers |= ModControl; break;
                    case "alt": modifiers |= ModAlt; break;
                    case "shift": modifiers |= ModShift; break;
                    case "win": modifiers |= ModWin; break;
                    default:
                        if (key != Keys.None)
                            throw new ConfigException("hotkey has two keys in it: " + text);
                        key = ParseKey(part, text);
                        break;
                }
            }

            if (key == Keys.None)
                throw new ConfigException("hotkey needs a key, e.g. ctrl+alt+p or F9. Got: " + text);

            return new HotkeySpec(modifiers, (uint)key, Describe(modifiers, key));
        }

        private static Keys ParseKey(string part, string whole)
        {
            // "5" on its own is the digit key; the Keys enum calls it D5.
            if (part.Length == 1 && char.IsDigit(part[0])) part = "d" + part;

            Keys parsed;
            try
            {
                parsed = (Keys)Enum.Parse(typeof(Keys), part, true);
            }
            catch (ArgumentException)
            {
                throw new ConfigException("Unknown key '" + part + "' in hotkey: " + whole);
            }

            var bare = parsed & Keys.KeyCode;
            if (bare == Keys.None || parsed != bare
                || bare == Keys.ControlKey || bare == Keys.ShiftKey || bare == Keys.Menu
                || bare == Keys.LWin || bare == Keys.RWin)
                throw new ConfigException("'" + part + "' can't be the main key of a hotkey: " + whole);

            return bare;
        }

        /// <summary>
        /// A combo pressed in a hotkey box, as config text ("ctrl+alt+p", "shift+F10"), or null
        /// while only modifiers are down. Parse reads it back to the same keys.
        /// </summary>
        public static string FromKeys(Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            if (key == Keys.None || key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu
                || key == Keys.LWin || key == Keys.RWin)
                return null;
            var name = key.ToString();
            if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name.Substring(1);
            else if (name.Length == 1) name = name.ToLowerInvariant();
            return Held(keyData) + name;
        }

        /// <summary>The modifiers held so far, e.g. "ctrl+alt+", so the box shows the combo as it's built.</summary>
        public static string Held(Keys keyData)
        {
            var text = "";
            if ((keyData & Keys.Control) != 0) text += "ctrl+";
            if ((keyData & Keys.Alt) != 0) text += "alt+";
            if ((keyData & Keys.Shift) != 0) text += "shift+";
            return text;
        }

        private static string Describe(uint modifiers, Keys key)
        {
            var text = "";
            if ((modifiers & ModControl) != 0) text += "Ctrl+";
            if ((modifiers & ModAlt) != 0) text += "Alt+";
            if ((modifiers & ModShift) != 0) text += "Shift+";
            if ((modifiers & ModWin) != 0) text += "Win+";
            var name = key.ToString();
            if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name.Substring(1);
            return text + name;
        }
    }
}
