using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Rewind
{
    /// <summary>
    /// The tray icon and everything hanging off it: the capture pipeline, the hotkey, the
    /// "--save" signal from other processes, and the balloon that says a clip landed.
    /// </summary>
    internal sealed class TrayApp : IDisposable
    {
        private const int MaxTooltipLength = 63; // NotifyIcon.Text refuses anything longer

        private readonly string _appDir;
        private readonly string _configPath;
        private Config _config;
        private string _ffmpeg;
        private ChunkRing _ring;
        private List<AudioTap> _taps = new List<AudioTap>();
        private Recorder _recorder;
        private NotifyIcon _icon;
        private Icon _recordingIcon;
        private Icon _pausedIcon;
        private ToolStripMenuItem _saveItem;
        private ToolStripMenuItem _pauseItem;
        private HotkeyWindow _hotkeyWindow;
        private Control _ui;
        private EventWaitHandle _saveEvent;
        private EventWaitHandle _quitEvent;
        private Thread _saveSignalThread;
        private System.Windows.Forms.Timer _tooltipTimer;
        private int _saving;
        private bool _userPaused;
        private bool _lockPaused;
        private volatile bool _disposed;

        public TrayApp(string appDir)
        {
            if (string.IsNullOrEmpty(appDir)) throw new ArgumentException("appDir");
            _appDir = appDir;
            _configPath = Path.Combine(appDir, "config.txt");
        }

        /// <summary>False (after telling the user why) when Rewind can't run at all.</summary>
        public bool Start()
        {
            try
            {
                _config = Config.Load(_configPath);
            }
            catch (ConfigException error)
            {
                Fatal("config.txt has a problem:\n\n" + error.Message + "\n\nFix it and start Rewind again.");
                return false;
            }
            catch (IOException error)
            {
                Fatal("Couldn't read config.txt: " + error.Message);
                return false;
            }

            try
            {
                _ffmpeg = FfmpegLocator.Find(_config.FfmpegPath);
            }
            catch (InvalidOperationException error)
            {
                Fatal(error.Message);
                return false;
            }

            _ui = new Control();
            var forceHandle = _ui.Handle; // a handle is what lets other threads Invoke onto this one
            GC.KeepAlive(forceHandle);

            BuildTray();
            OpenPipeline();
            RegisterHotkey();
            StartSaveSignal();
            SystemEvents.SessionSwitch += OnSessionSwitch;

            _tooltipTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _tooltipTimer.Tick += (s, e) => RefreshTooltip();
            _tooltipTimer.Start();

            Log.Info(string.Format("Rewind started: {0} s buffer, {1} fps, {2} Mbps {3}, hotkey {4}, clips -> {5}",
                _config.Seconds, _config.Fps, _config.BitrateMbps, _config.Codec, _config.Hotkey, _config.ClipsFolder));
            return true;
        }

        // ---- pipeline ----

        private void OpenPipeline()
        {
            _ring = new ChunkRing(TimeSpan.FromSeconds(_config.Seconds + 2));
            _taps = new List<AudioTap>();
            if (_config.GameAudio) OpenTap("Game", "rewind_game", true, "");
            if (_config.Mic) OpenTap("Mic", "rewind_mic", false, _config.MicFilter);
            _recorder = new Recorder(_config, _ring, _ffmpeg, _taps);
            _recorder.Start();
        }

        private void OpenTap(string label, string pipeName, bool loopback, string filter)
        {
            var tap = new AudioTap(label, pipeName, loopback, filter);
            try
            {
                tap.Open(TimeSpan.FromSeconds(5));
                _taps.Add(tap);
            }
            catch (InvalidOperationException error)
            {
                tap.Dispose();
                Log.Warn(error.Message);
                Balloon("No " + label.ToLowerInvariant() + " audio", error.Message + " Clips will be saved without it.", ToolTipIcon.Warning);
            }
        }

        private void ClosePipeline()
        {
            if (_recorder != null)
            {
                _recorder.Dispose();
                _recorder = null;
            }
            foreach (var tap in _taps) tap.Dispose();
            _taps = new List<AudioTap>();
        }

        private void ReloadConfig()
        {
            Config fresh;
            try
            {
                fresh = Config.Load(_configPath);
            }
            catch (ConfigException error)
            {
                Balloon("config.txt not reloaded", error.Message, ToolTipIcon.Error);
                return;
            }
            catch (IOException error)
            {
                Balloon("config.txt not reloaded", error.Message, ToolTipIcon.Error);
                return;
            }

            Log.Info("reloading config");
            ClosePipeline();
            _config = fresh;
            OpenPipeline();
            RegisterHotkey();
            if (_userPaused) _recorder.Pause();
            Balloon("Config reloaded", string.Format("{0} s clips, hotkey {1}", _config.Seconds, HotkeySpec.Parse(_config.Hotkey).Text), ToolTipIcon.Info);
        }

        // ---- hotkey + external save signal ----

        private void RegisterHotkey()
        {
            var spec = HotkeySpec.Parse(_config.Hotkey);
            if (_hotkeyWindow == null)
            {
                _hotkeyWindow = new HotkeyWindow();
                _hotkeyWindow.Pressed += (s, e) => SaveClip("hotkey");
            }
            string error;
            if (_hotkeyWindow.Register(spec, out error))
            {
                _saveItem.Text = "Save clip now  (" + spec.Text + ")";
                return;
            }
            Log.Warn(error);
            _saveItem.Text = "Save clip now  (no hotkey)";
            Balloon("Hotkey not available", error + " Use the tray menu, or change hotkey= in config.txt.", ToolTipIcon.Warning);
        }

        private void StartSaveSignal()
        {
            bool created;
            _saveEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.SaveEventName, out created);
            _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEventName, out created);
            var signals = new WaitHandle[] { _saveEvent, _quitEvent };
            _saveSignalThread = new Thread(() =>
            {
                while (!_disposed)
                {
                    var fired = WaitHandle.WaitAny(signals, 500);
                    if (_disposed) break;
                    if (fired == 0) SaveClip("--save");
                    else if (fired == 1)
                    {
                        Log.Info("quit from --quit");
                        OnUi(Quit);
                        break;
                    }
                }
            }) { IsBackground = true, Name = "rewind-signals" };
            _saveSignalThread.Start();
        }

        // ---- saving ----

        public void SaveClip(string reason)
        {
            var recorder = _recorder;
            if (recorder == null || recorder.Paused)
            {
                OnUi(() => Balloon("Not recording", "Rewind is paused, so there's nothing to save.", ToolTipIcon.Warning));
                return;
            }
            if (Interlocked.CompareExchange(ref _saving, 1, 0) != 0)
            {
                OnUi(() => Balloon("Hold on", "Still saving the last clip.", ToolTipIcon.Info));
                return;
            }

            var labels = new List<string>();
            foreach (var tap in _taps) labels.Add(tap.Label);
            var config = _config;
            var ffmpeg = _ffmpeg;
            var ring = _ring;

            var worker = new Thread(() =>
            {
                try
                {
                    var clip = ClipSaver.Save(ring, config, ffmpeg, labels);
                    Log.Info(string.Format("clip saved ({0}): {1} ({2:0.0} MB, buffer held {3:0} s)",
                        reason, clip.Path, clip.Bytes / 1048576.0, clip.Buffered.TotalSeconds));
                    OnUi(() =>
                    {
                        SystemSounds.Asterisk.Play();
                        Balloon("Clip saved", Path.GetFileName(clip.Path), ToolTipIcon.Info);
                    });
                }
                catch (Exception error)
                {
                    Log.Error("clip save failed: " + error);
                    OnUi(() => Balloon("Clip NOT saved", error.Message, ToolTipIcon.Error));
                }
                finally
                {
                    Interlocked.Exchange(ref _saving, 0);
                    // The snapshot was a ~150 MB array; hand it back now rather than whenever.
                    GC.Collect();
                }
            }) { IsBackground = true, Name = "rewind-save" };
            worker.Start();
        }

        // ---- tray ----

        private void BuildTray()
        {
            _recordingIcon = DrawIcon(Color.FromArgb(230, 40, 40));
            _pausedIcon = DrawIcon(Color.FromArgb(120, 120, 120));

            var menu = new ContextMenuStrip();
            _saveItem = new ToolStripMenuItem("Save clip now", null, (s, e) => SaveClip("menu"));
            _saveItem.Font = new Font(_saveItem.Font, FontStyle.Bold);
            _pauseItem = new ToolStripMenuItem("Pause recording", null, (s, e) => TogglePause());
            menu.Items.Add(_saveItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_pauseItem);
            menu.Items.Add(new ToolStripMenuItem("Open clips folder", null, (s, e) => OpenClipsFolder()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Open config.txt", null, (s, e) => OpenFile(_configPath)));
            menu.Items.Add(new ToolStripMenuItem("Reload config", null, (s, e) => ReloadConfig()));
            menu.Items.Add(new ToolStripMenuItem("Open log", null, (s, e) => OpenFile(Log.Path)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Quit Rewind", null, (s, e) => Quit()));

            _icon = new NotifyIcon
            {
                Icon = _recordingIcon,
                Text = "Rewind",
                ContextMenuStrip = menu,
                Visible = true
            };
            _icon.DoubleClick += (s, e) => SaveClip("double-click");
        }

        private static Icon DrawIcon(Color fill)
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, 3, 3, 26, 26);
                using (var pen = new Pen(Color.FromArgb(255, 255, 255), 2.5f)) g.DrawArc(pen, 9, 9, 14, 14, 20, 300);
                using (var brush = new SolidBrush(Color.White)) g.FillPolygon(brush, new[] { new Point(20, 6), new Point(26, 12), new Point(18, 13) });
                using (var icon = Icon.FromHandle(bitmap.GetHicon())) return (Icon)icon.Clone();
            }
        }

        private void RefreshTooltip()
        {
            var recorder = _recorder;
            var ring = _ring;
            if (_icon == null || recorder == null || ring == null) return;
            var text = string.Format("Rewind: {0}, {1:0} s buffered ({2:0} MB)",
                recorder.Status, ring.Span.TotalSeconds, ring.Bytes / 1048576.0);
            if (text.Length > MaxTooltipLength) text = text.Substring(0, MaxTooltipLength);
            if (_icon.Text != text) _icon.Text = text;
            var wanted = recorder.Paused ? _pausedIcon : _recordingIcon;
            if (_icon.Icon != wanted) _icon.Icon = wanted;
        }

        private void TogglePause()
        {
            if (_recorder == null) return;
            _userPaused = !_userPaused;
            if (_userPaused) _recorder.Pause(); else _recorder.Resume();
            _pauseItem.Text = _userPaused ? "Resume recording" : "Pause recording";
            Log.Info(_userPaused ? "paused by user" : "resumed by user");
            RefreshTooltip();
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (_recorder == null) return;
            if (e.Reason == SessionSwitchReason.SessionLock && !_userPaused)
            {
                // Desktop Duplication can't see the lock screen anyway; stop trying until it's back.
                _lockPaused = true;
                _recorder.Pause();
                Log.Info("paused: session locked");
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock && _lockPaused)
            {
                _lockPaused = false;
                if (!_userPaused) _recorder.Resume();
                Log.Info("resumed: session unlocked");
            }
        }

        private void OpenClipsFolder()
        {
            try
            {
                Directory.CreateDirectory(_config.ClipsFolder);
                Process.Start("explorer.exe", FfmpegArgs.Quote(_config.ClipsFolder));
            }
            catch (Exception error)
            {
                Balloon("Couldn't open folder", error.Message, ToolTipIcon.Error);
            }
        }

        private void OpenFile(string path)
        {
            try
            {
                if (path != null && !File.Exists(path)) File.WriteAllText(path, "");
                Process.Start("notepad.exe", FfmpegArgs.Quote(path));
            }
            catch (Exception error)
            {
                Balloon("Couldn't open file", error.Message, ToolTipIcon.Error);
            }
        }

        private void Balloon(string title, string text, ToolTipIcon kind)
        {
            if (_icon == null) return;
            _icon.ShowBalloonTip(4000, title, text, kind);
        }

        private static void Fatal(string message)
        {
            Log.Error(message.Replace("\n", " "));
            MessageBox.Show(message, "Rewind can't start", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void OnUi(Action action)
        {
            var ui = _ui;
            if (ui == null || _disposed) return;
            try
            {
                if (ui.InvokeRequired) ui.BeginInvoke(action); else action();
            }
            catch (InvalidOperationException)
            {
                // The UI is shutting down: the balloon can be skipped.
            }
        }

        private void Quit()
        {
            Log.Info("quit from tray");
            Dispose();
            Application.Exit();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            if (_tooltipTimer != null) _tooltipTimer.Dispose();
            if (_hotkeyWindow != null) _hotkeyWindow.Dispose();
            ClosePipeline();
            if (_saveEvent != null) _saveEvent.Dispose();
            if (_quitEvent != null) _quitEvent.Dispose();
            if (_icon != null)
            {
                _icon.Visible = false;
                _icon.Dispose();
            }
            if (_ui != null) _ui.Dispose();
        }
    }
}
