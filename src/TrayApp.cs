using System;
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
    /// The tray icon and everything hanging off it: the capture session, the hotkeys, the
    /// "--save" signals from other processes, the game watch, the audio-device retry, and the
    /// balloon that says a clip landed.
    /// </summary>
    internal sealed class TrayApp : IDisposable
    {
        private const int MaxTooltipLength = 63; // NotifyIcon.Text refuses anything longer
        private const int TickMs = 2000;
        private const int ProbeMs = 15000;

        private readonly string _appDir;
        private readonly string _configPath;
        private Config _config;
        private string _ffmpeg;
        private CaptureSession _session;
        private NotifyIcon _icon;
        private Icon _recordingIcon;
        private Icon _pausedIcon;
        private Icon _waitingIcon;
        private ToolStripMenuItem _saveItem;
        private ToolStripMenuItem _saveShortItem;
        private ToolStripMenuItem _pauseItem;
        private HotkeyWindow _hotkeyWindow;
        private Control _ui;
        private EventWaitHandle _saveEvent;
        private EventWaitHandle _saveShortEvent;
        private EventWaitHandle _quitEvent;
        private Thread _signalThread;
        private System.Windows.Forms.Timer _tickTimer;
        private System.Windows.Forms.Timer _probeTimer;
        private Action _balloonAction;
        private DateTime _lastGameSeenUtc = DateTime.MinValue;
        private bool _probing;
        private volatile bool _disposed;

        public TrayApp(string appDir)
        {
            if (string.IsNullOrEmpty(appDir)) throw new ArgumentException("appDir");
            _appDir = appDir;
            _configPath = Path.Combine(appDir, "config.txt");
        }

        public Config Config { get { return _config; } }
        public SessionStatus Status { get { return _session != null ? _session.Status : null; } }

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
            _session = new CaptureSession(_config, _ffmpeg);
            _session.AudioMissing += OnAudioMissing;
            if (_config.GamesOnly)
            {
                // Decided before Start so no ffmpeg spins up for nothing.
                var info = ForegroundApp.Probe();
                var game = GameDetector.IsGame(info, _config.Games);
                _session.SetGamePaused(!game);
                Log.Info(game ? "record=games: game in front (" + info.ProcessName + "), recording" : "record=games: no game in front, waiting for one");
            }
            _session.Start();
            RegisterHotkeys();
            StartSignals();
            SystemEvents.SessionSwitch += OnSessionSwitch;

            _tickTimer = new System.Windows.Forms.Timer { Interval = TickMs };
            _tickTimer.Tick += (s, e) => Tick();
            _tickTimer.Start();
            _probeTimer = new System.Windows.Forms.Timer { Interval = ProbeMs };
            _probeTimer.Tick += (s, e) => ProbeMissingAudio();
            _probeTimer.Start();

            Log.Info(string.Format("Rewind started: {0} s buffer, {1} fps, {2} Mbps {3}, hotkey {4}, short clip {5} s on {6}, record={7}, clips -> {8}",
                _config.Seconds, _config.Fps, _config.BitrateMbps, _config.Codec, _config.Hotkey,
                _config.ShortSeconds, _config.HotkeyShort.Length > 0 ? _config.HotkeyShort : "(no key)", _config.Record, _config.ClipsFolder));
            return true;
        }

        // ---- config ----

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
            ApplyConfig(fresh);
            Balloon("Config reloaded", string.Format("{0} s clips, hotkey {1}", _config.Seconds, HotkeySpec.Parse(_config.Hotkey).Text), ToolTipIcon.Info);
        }

        /// <summary>Puts a new config live: the pipeline restarts (buffer starts empty), hotkeys re-register.</summary>
        public void ApplyConfig(Config fresh)
        {
            if (fresh == null) throw new ArgumentNullException("fresh");
            Log.Info("reloading config");
            _config = fresh;
            _session.Rebuild(fresh);
            RegisterHotkeys();
            WatchGame();
            RefreshTooltip();
        }

        // ---- hotkeys + external signals ----

        private void RegisterHotkeys()
        {
            if (_hotkeyWindow == null)
            {
                _hotkeyWindow = new HotkeyWindow();
                _hotkeyWindow.Pressed += (s, e) =>
                {
                    if (e.Slot == 0) SaveClip("hotkey", _config.Seconds);
                    else SaveClip("short hotkey", _config.ShortSeconds);
                };
            }

            _saveItem.Text = "Save clip now  (" + Register(0, HotkeySpec.Parse(_config.Hotkey)) + ")";
            _saveShortItem.Text = "Save last " + _config.ShortSeconds + " s";
            if (_config.HotkeyShort.Length > 0)
                _saveShortItem.Text += "  (" + Register(1, HotkeySpec.Parse(_config.HotkeyShort)) + ")";
            else
                _hotkeyWindow.Unregister(1);
        }

        /// <summary>Claims a hotkey and returns its text for the menu, or "no hotkey" after explaining why.</summary>
        private string Register(int slot, HotkeySpec spec)
        {
            string error;
            if (_hotkeyWindow.Register(slot, spec, out error)) return spec.Text;
            Log.Warn(error);
            Balloon("Hotkey not available", error + " Use the tray menu, or change the hotkey in config.txt.", ToolTipIcon.Warning);
            return "no hotkey";
        }

        private void StartSignals()
        {
            bool created;
            _saveEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.SaveEventName, out created);
            _saveShortEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.SaveShortEventName, out created);
            _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEventName, out created);
            var signals = new WaitHandle[] { _saveEvent, _saveShortEvent, _quitEvent };
            _signalThread = new Thread(() =>
            {
                while (!_disposed)
                {
                    var fired = WaitHandle.WaitAny(signals, 500);
                    if (_disposed) break;
                    if (fired == 0) SaveClip("--save", _config.Seconds);
                    else if (fired == 1) SaveClip("--save-short", _config.ShortSeconds);
                    else if (fired == 2)
                    {
                        Log.Info("quit from --quit");
                        OnUi(Quit);
                        break;
                    }
                }
            }) { IsBackground = true, Name = "rewind-signals" };
            _signalThread.Start();
        }

        // ---- saving ----

        public void SaveClip(string reason, int seconds)
        {
            var config = _config;
            var result = _session.SaveAsync(seconds, reason,
                clip => OnUi(() =>
                {
                    SystemSounds.Asterisk.Play();
                    var title = seconds == config.Seconds ? "Clip saved" : "Clip saved (" + seconds + " s)";
                    Balloon(title, Path.GetFileName(clip.Path) + "\nClick to show it in the folder.", ToolTipIcon.Info,
                        () => ShowInFolder(clip.Path));
                }),
                error => OnUi(() => Balloon("Clip NOT saved", error.Message + "\nClick to open the log.", ToolTipIcon.Error,
                    () => OpenFile(Log.Path))));

            if (result == SaveStart.Paused)
            {
                var why = _session.PauseReason.Length > 0 ? _session.PauseReason : "not running";
                OnUi(() => Balloon("Not recording", "Rewind is " + why + ", so there's nothing to save.", ToolTipIcon.Warning));
            }
            else if (result == SaveStart.Busy)
            {
                OnUi(() => Balloon("Hold on", "Still saving the last clip.", ToolTipIcon.Info));
            }
        }

        // ---- game watch + audio retry (every 2 s / 15 s on the UI thread) ----

        private void Tick()
        {
            if (_disposed) return;
            WatchGame();
            RefreshTooltip();
        }

        private bool GameInFront()
        {
            return GameDetector.IsGame(ForegroundApp.Probe(), _config.Games);
        }

        /// <summary>In games mode: record while a game is in front, pause once it has been gone for the grace period.</summary>
        private void WatchGame()
        {
            var info = ForegroundApp.Probe();
            var game = GameDetector.IsGame(info, _config.Games);
            var now = DateTime.UtcNow;
            if (game) _lastGameSeenUtc = now;

            if (!_config.GamesOnly)
            {
                if (_session.GamePaused) _session.SetGamePaused(false);
                return;
            }
            if (game && _session.GamePaused)
            {
                Log.Info("game in front (" + info.ProcessName + "): recording");
                _session.SetGamePaused(false);
            }
            else if (!game && !_session.GamePaused && now - _lastGameSeenUtc > TimeSpan.FromSeconds(_config.GameGraceSeconds))
            {
                Log.Info("no game in front for " + _config.GameGraceSeconds + " s: waiting for one");
                _session.SetGamePaused(true);
            }
        }

        private void ProbeMissingAudio()
        {
            if (_disposed || _probing || _session.MissingTaps.Count == 0) return;
            _probing = true;
            var session = _session;
            var worker = new Thread(() =>
            {
                TapSpec found = null;
                try
                {
                    found = session.ProbeMissing();
                }
                catch (Exception error)
                {
                    Log.Warn("audio device check failed: " + error.Message);
                }
                OnUi(() =>
                {
                    _probing = false;
                    if (found == null || _disposed) return;
                    Log.Info(found.Label + " audio is back; restarting capture with it");
                    _session.Rebuild(_config);
                    Balloon(found.Label + " audio connected", "Clips now include it.", ToolTipIcon.Info);
                });
            }) { IsBackground = true, Name = "rewind-audio-probe" };
            worker.Start();
        }

        private void OnAudioMissing(TapSpec spec, string message)
        {
            Balloon("No " + spec.Label.ToLowerInvariant() + " audio",
                message + " Clips will be saved without it; Rewind keeps checking for it.", ToolTipIcon.Warning);
        }

        // ---- tray ----

        private void BuildTray()
        {
            _recordingIcon = DrawIcon(Color.FromArgb(230, 40, 40));
            _pausedIcon = DrawIcon(Color.FromArgb(120, 120, 120));
            _waitingIcon = DrawIcon(Color.FromArgb(235, 160, 30));

            var menu = new ContextMenuStrip();
            _saveItem = new ToolStripMenuItem("Save clip now", null, (s, e) => SaveClip("menu", _config.Seconds));
            _saveItem.Font = new Font(_saveItem.Font, FontStyle.Bold);
            _saveShortItem = new ToolStripMenuItem("Save short clip", null, (s, e) => SaveClip("menu", _config.ShortSeconds));
            _pauseItem = new ToolStripMenuItem("Pause recording", null, (s, e) => TogglePause());
            menu.Items.Add(_saveItem);
            menu.Items.Add(_saveShortItem);
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
            _icon.DoubleClick += (s, e) => SaveClip("double-click", _config.Seconds);
            _icon.BalloonTipClicked += (s, e) =>
            {
                var action = _balloonAction;
                _balloonAction = null;
                if (action != null) action();
            };
            _icon.BalloonTipClosed += (s, e) => _balloonAction = null;
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
            if (_icon == null || _session == null) return;
            var status = _session.Status;
            var text = string.Format("Rewind: {0}, {1:0} s buffered ({2:0} MB)", status.Text, status.BufferedSeconds, status.BufferedMb);
            if (text.Length > MaxTooltipLength) text = text.Substring(0, MaxTooltipLength);
            if (_icon.Text != text) _icon.Text = text;
            var wanted = status.PauseReason == "waiting for a game" ? _waitingIcon : status.Paused ? _pausedIcon : _recordingIcon;
            if (_icon.Icon != wanted) _icon.Icon = wanted;
        }

        public void TogglePause()
        {
            var paused = !_session.UserPaused;
            _session.SetUserPaused(paused);
            _pauseItem.Text = paused ? "Resume recording" : "Pause recording";
            Log.Info(paused ? "paused by user" : "resumed by user");
            RefreshTooltip();
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (_session == null) return;
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                // Desktop Duplication can't see the lock screen anyway; stop trying until it's back.
                _session.SetLockPaused(true);
                Log.Info("paused: session locked");
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock && _session.LockPaused)
            {
                _session.SetLockPaused(false);
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

        private void ShowInFolder(string path)
        {
            try
            {
                Process.Start("explorer.exe", Shell.SelectInExplorerArgs(path));
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
            Balloon(title, text, kind, null);
        }

        /// <summary>Shows a balloon; onClick (if any) runs when the user clicks it.</summary>
        private void Balloon(string title, string text, ToolTipIcon kind, Action onClick)
        {
            if (_icon == null) return;
            _balloonAction = onClick;
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
            if (_tickTimer != null) _tickTimer.Dispose();
            if (_probeTimer != null) _probeTimer.Dispose();
            if (_hotkeyWindow != null) _hotkeyWindow.Dispose();
            if (_session != null) _session.Dispose();
            if (_saveEvent != null) _saveEvent.Dispose();
            if (_saveShortEvent != null) _saveShortEvent.Dispose();
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
