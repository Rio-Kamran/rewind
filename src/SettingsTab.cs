using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Rewind
{
    /// <summary>
    /// The Settings tab: one row per config.txt setting, Apply writes the file back (with its
    /// comments) and restarts capture. Validation is Config.Parse, so the window and the text
    /// file can never disagree about what's allowed.
    /// </summary>
    internal sealed class SettingsTab : UserControl
    {
        private readonly IRewindControl _control;
        private readonly TableLayoutPanel _grid = new TableLayoutPanel();
        private readonly Dictionary<string, Control> _inputs = new Dictionary<string, Control>();
        private readonly Label _message = new Label();
        private readonly Button _apply = new Button();

        public SettingsTab(IRewindControl control)
        {
            if (control == null) throw new ArgumentNullException("control");
            _control = control;
            AutoScroll = true;

            _grid.ColumnCount = 3;
            _grid.AutoSize = true;
            _grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _grid.Padding = new Padding(12, 12, 12, 12);
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            AddText("hotkey", "Save clip hotkey", "e.g. ctrl+alt+p, F9, shift+F10");
            AddText("hotkey_short", "Short clip hotkey", "off = no second key");
            AddNumber("seconds", "Clip length (s)", Config.MinSeconds, Config.MaxSeconds, "how far back a clip reaches");
            AddNumber("short_seconds", "Short clip length (s)", Config.MinShortSeconds, Config.MaxSeconds - 1, "must be less than the clip length");
            AddNumber("fps", "Frames per second", Config.MinFps, Config.MaxFps, "60 matches most games");
            AddNumber("bitrate_mbps", "Quality (Mbps)", Config.MinBitrate, Config.MaxBitrate, "20 = ~150 MB per minute at 1440p");
            AddChoice("codec", "Video codec", new[] { "h264", "hevc", "av1" }, "h264 plays everywhere; av1 = half the size");
            AddChoice("monitor", "Monitor", MonitorChoices(), "primary = the main monitor");
            AddCheck("game_audio", "Record game audio", "what the headset hears (track 1)");
            AddCheck("mic", "Record the mic", "your voice (track 2)");
            AddText("mic_filter", "Mic cleanup filter", "an ffmpeg audio filter; off = raw mic");
            AddNumber("audio_offset_ms", "Audio offset (ms)", -Config.MaxAudioOffsetMs, Config.MaxAudioOffsetMs, "+ = sound later than picture");
            AddChoice("record", "Record", new[] { Config.RecordAlways, Config.RecordGames }, "games = only while a game is in front");
            AddText("games", "Games list", "process names, comma separated; fullscreen apps count anyway");
            AddNumber("game_grace_seconds", "Game grace (s)", Config.MinGraceSeconds, Config.MaxGraceSeconds, "how long a game can be out of front before pausing");
            AddFolder("clips", "Clips folder");
            AddText("ffmpeg", "ffmpeg.exe", "empty = the one on PATH");

            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
            _apply.Text = "Apply";
            _apply.Font = new Font(Font, FontStyle.Bold);
            _apply.AutoSize = true;
            _apply.Click += (s, e) => Apply();
            var reset = new Button { Text = "Reset to defaults", AutoSize = true };
            reset.Click += (s, e) => LoadFrom(Config.Defaults());
            var open = new Button { Text = "Open config.txt", AutoSize = true };
            open.Click += (s, e) => OpenConfigFile();
            buttons.Controls.AddRange(new Control[] { _apply, reset, open });
            _grid.Controls.Add(buttons, 1, _grid.RowCount);
            _grid.RowCount++;

            _message.AutoSize = true;
            _message.MaximumSize = new Size(480, 0);
            _message.Margin = new Padding(0, 8, 0, 0);
            _grid.Controls.Add(_message, 1, _grid.RowCount);
            _grid.SetColumnSpan(_message, 2);
            _grid.RowCount++;

            Controls.Add(_grid);
        }

        /// <summary>Puts a config's values into the controls.</summary>
        public void LoadFrom(Config config)
        {
            if (config == null) throw new ArgumentNullException("config");
            foreach (var pair in config.Values())
            {
                Control input;
                if (!_inputs.TryGetValue(pair.Key, out input)) continue;
                var number = input as NumericUpDown;
                var check = input as CheckBox;
                var choice = input as ComboBox;
                if (number != null)
                {
                    decimal value;
                    if (decimal.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                        number.Value = Math.Max(number.Minimum, Math.Min(number.Maximum, value));
                }
                else if (check != null) check.Checked = pair.Value == "on";
                else if (choice != null) SelectChoice(choice, pair.Value);
                else input.Text = pair.Value;
            }
            _message.Text = "";
        }

        /// <summary>What the controls say, as config.txt strings.</summary>
        public IDictionary<string, string> Values()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _inputs)
            {
                var number = pair.Value as NumericUpDown;
                var check = pair.Value as CheckBox;
                var choice = pair.Value as ComboBox;
                if (number != null) values[pair.Key] = ((int)number.Value).ToString(CultureInfo.InvariantCulture);
                else if (check != null) values[pair.Key] = check.Checked ? "on" : "off";
                else if (choice != null) values[pair.Key] = ChoiceValue(choice);
                else values[pair.Key] = pair.Value.Text.Trim();
            }
            return values;
        }

        private void Apply()
        {
            try
            {
                _control.SaveSettings(Values());
                _message.ForeColor = Color.ForestGreen;
                _message.Text = "Saved. Recording restarted with the new settings (the buffer starts again from empty).";
            }
            catch (ConfigException error)
            {
                _message.ForeColor = Color.Firebrick;
                _message.Text = error.Message;
            }
            catch (Exception error)
            {
                _message.ForeColor = Color.Firebrick;
                _message.Text = "Couldn't save: " + error.Message;
            }
        }

        private void OpenConfigFile()
        {
            try { Process.Start("notepad.exe", FfmpegArgs.Quote(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt"))); }
            catch (Exception error) { _message.ForeColor = Color.Firebrick; _message.Text = error.Message; }
        }

        // ---- rows ----

        private void AddRow(string key, string label, Control input, string hint)
        {
            var row = _grid.RowCount;
            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var caption = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) };
            input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            input.Margin = new Padding(0, 3, 8, 3);
            var help = new Label { Text = hint ?? "", AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 6) };
            _grid.Controls.Add(caption, 0, row);
            _grid.Controls.Add(input, 1, row);
            _grid.Controls.Add(help, 2, row);
            _grid.RowCount++;
            _inputs[key] = input;
        }

        private void AddText(string key, string label, string hint)
        {
            AddRow(key, label, new TextBox(), hint);
        }

        private void AddNumber(string key, string label, int min, int max, string hint)
        {
            AddRow(key, label, new NumericUpDown { Minimum = min, Maximum = max, Width = 100, Anchor = AnchorStyles.Left }, hint);
        }

        private void AddCheck(string key, string label, string hint)
        {
            AddRow(key, label, new CheckBox { Text = hint, AutoSize = true }, "");
        }

        private void AddChoice(string key, string label, IList<string> choices, string hint)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var choice in choices) combo.Items.Add(choice);
            AddRow(key, label, combo, hint);
        }

        private void AddFolder(string key, string label)
        {
            var panel = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var box = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 3, 4, 3) };
            var browse = new Button { Text = "Browse…", AutoSize = true, Margin = new Padding(0, 1, 0, 1) };
            browse.Click += (s, e) =>
            {
                using (var dialog = new FolderBrowserDialog { SelectedPath = box.Text, Description = "Where clips go" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
            };
            panel.Controls.Add(box, 0, 0);
            panel.Controls.Add(browse, 1, 0);
            AddRow(key, label, panel, "");
            _inputs[key] = box; // the text box is the value; the panel is only layout
        }

        // ---- choices: "0: \\.\DISPLAY2 2560x1440 (primary)" shows, "0" is the value ----

        private static IList<string> MonitorChoices()
        {
            var choices = new List<string> { "primary" };
            try
            {
                foreach (var output in Dxgi.ListOutputs()) choices.Add(output.ToString());
            }
            catch (Exception error)
            {
                Log.Warn("couldn't list monitors for settings: " + error.Message);
            }
            return choices;
        }

        private static void SelectChoice(ComboBox combo, string value)
        {
            for (var i = 0; i < combo.Items.Count; i++)
            {
                if (string.Equals(ValueOf((string)combo.Items[i]), value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
        }

        private static string ChoiceValue(ComboBox combo)
        {
            return combo.SelectedItem != null ? ValueOf((string)combo.SelectedItem) : "";
        }

        /// <summary>"0: \\.\DISPLAY2 ..." -> "0"; anything without a colon is its own value.</summary>
        private static string ValueOf(string choice)
        {
            var colon = choice.IndexOf(':');
            return colon > 0 && char.IsDigit(choice[0]) ? choice.Substring(0, colon) : choice;
        }
    }
}
