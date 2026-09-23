using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Rewind
{
    /// <summary>
    /// The tray icon and everything hanging off it: the capture session, the hotkeys, the
    /// "--save" signals from other processes, the game watch, the audio-device retry, the voice
    /// trigger, the long recording, and the card / balloon / chime that says something landed.
    /// </summary>
    internal sealed class TrayApp : IDisposable, IRewindControl
    {
        private const int MaxTooltipLength = 63; // NotifyIcon.Text refuses anything longer
        private const int TickMs = 2000;
        private const int ProbeMs = 15000;
        private const int StopRecordingWaitMs = 120000;

        private readonly string _appDir;
        private readonly string _configPath;
        private Config _config;
        private string _ffmpeg;
        private CaptureSession _session;
        private IVoiceTrigger _voice;
        private NotifyIcon _icon;
        private Icon _recordingIcon;
        private Icon _longRecordingIcon;
        private Icon _pausedIcon;
        private Icon _waitingIcon;
        private ToolStripMenuItem _saveItem;
        private ToolStripMenuItem _saveShortItem;
        private ToolStripMenuItem _saveLastMenu;
        private ToolStripMenuItem _recordItem;
        private ToolStripMenuItem _screenshotItem;
        private ToolStripMenuItem _pauseItem;
        private HotkeyWindow _hotkeyWindow;
        private Control _ui;
        private EventWaitHandle _saveEvent;
        private EventWaitHandle _saveShortEvent;
        private EventWaitHandle _recordEvent;
        private EventWaitHandle _screenshotEvent;
        private EventWaitHandle _quitEvent;
        private EventWaitHandle _showEvent;
        private Thread _signalThread;
        private ClipsForm _window;
        private System.Windows.Forms.Timer _tickTimer;
        private System.Windows.Forms.Timer _probeTimer;
        private Action _balloonAction;
        private DateTime _lastGameSeenUtc = DateTime.MinValue;
        private string _lastInFront = "";
        private bool _probing;
        private bool _stoppingRecording;
        private int _pruning;
        private volatile bool _disposed;

        public TrayApp(string appDir)
        {
            if (string.IsNullOrEmpty(appDir)) throw new ArgumentException("appDir");
            _appDir = appDir;
            _configPath = Path.Combine(appDir, "config.txt");
        }

        public Config Config { get { return _config; } }
        public string FfmpegPath { get { return _ffmpeg; } }
        public string AppDir { get { return _appDir; } }
        public SessionStatus Status { get { return _session != null ? _session.Status : null; } }
        public bool UserPaused { get { return _session != null && _session.UserPaused; } }
        public bool Recording { get { return _session != null && _session.Recording; } }
        public event Action<SavedClip> ClipSaved;
        public event Action FilesChanged;

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

            _ui = new Control();
            var forceHandle = _ui.Handle; // a handle is what lets other threads Invoke onto this one
            GC.KeepAlive(forceHandle);

            BuildTray();

            string ffmpeg;
            try
            {
                ffmpeg = FfmpegLocator.TryFind(_config.FfmpegPath);
            }
            catch (InvalidOperationException error)
            {
                Fatal(error.Message);
                return false;
            }
            if (ffmpeg != null) StartCapture(ffmpeg);
            else FetchFfmpeg(); // one-time: capture starts when the download lands
            return true;
        }

        /// <summary>Everything that needs ffmpeg: the session, hotkeys, signals, the timers, the voice trigger.</summary>
        private void StartCapture(string ffmpeg)
        {
            _ffmpeg = ffmpeg;
            _session = new CaptureSession(_config, _ffmpeg);
            _session.AudioMissing += OnAudioMissing;
            _session.ClipSaved += clip => OnUi(() =>
            {
                var handler = ClipSaved;
                if (handler != null) handler(clip);
            });
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
            StartVoice();
            RecoverRecordings();
            SystemEvents.SessionSwitch += OnSessionSwitch;

            _tickTimer = new System.Windows.Forms.Timer { Interval = TickMs };
            _tickTimer.Tick += (s, e) => Tick();
            _tickTimer.Start();
            _probeTimer = new System.Windows.Forms.Timer { Interval = ProbeMs };
            _probeTimer.Tick += (s, e) => ProbeMissing();
            _probeTimer.Start();

            Log.Info(string.Format("Rewind started: {0} s buffer, {1} fps, {2} Mbps {3}, hotkey {4}, short clip {5} s on {6}, record {7}, screenshot {8}, record={9}, voice={10}, clips -> {11}",
                _config.Seconds, _config.Fps, _config.BitrateMbps, _config.Codec, _config.Hotkey,
                _config.ShortSeconds, KeyOrNone(_config.HotkeyShort), KeyOrNone(_config.HotkeyRecord), KeyOrNone(_config.HotkeyScreenshot),
                _config.Record, _config.VoiceClip ? "\"" + _config.VoicePhrase + "\" (" + _config.VoiceEngine + ")" : "off", _config.ClipsFolder));
            RefreshTooltip();
        }

        private static string KeyOrNone(string hotkey)
        {
            return hotkey.Length > 0 ? hotkey : "(no key)";
        }

        /// <summary>No ffmpeg anywhere: download it (about 110 MB, once) and then start capture.</summary>
        private void FetchFfmpeg()
        {
            _icon.Icon = _pausedIcon;
            SetTooltip("Rewind: getting ffmpeg");
            Log.Info("ffmpeg not found; downloading it from " + FfmpegFetcher.ZipUrl + " into " + FfmpegFetcher.DefaultFolder);
            Balloon("One-time setup", "Rewind needs ffmpeg (about 110 MB). Downloading it now; recording starts when it's done.", ToolTipIcon.Info);
            var folder = FfmpegFetcher.DefaultFolder;
            var worker = new Thread(() =>
            {
                string path = null;
                string failure = null;
                try
                {
                    path = FfmpegFetcher.Fetch(folder, text => OnUi(() => SetTooltip("Rewind: " + text)));
                }
                catch (Exception error)
                {
                    failure = error.Message;
                }
                OnUi(() =>
                {
                    if (_disposed) return;
                    if (path == null)
                    {
                        Log.Error("ffmpeg download failed: " + failure);
                        SetTooltip("Rewind: no ffmpeg (click the balloon)");
                        Balloon("Couldn't get ffmpeg", failure + "\nRewind will try again next start. Click to open the log.", ToolTipIcon.Error,
                            () => OpenFile(Log.Path));
                        return;
                    }
                    Log.Info("ffmpeg ready: " + path);
                    StartCapture(path);
                    Balloon("Ready", "ffmpeg is in place and Rewind is recording. " + HotkeySpec.Parse(_config.Hotkey).Text + " saves a clip.", ToolTipIcon.Info);
                });
            }) { IsBackground = true, Name = "rewind-ffmpeg-fetch" };
            worker.Start();
        }

        private void SetTooltip(string text)
        {
            if (_icon == null) return;
            if (text.Length > MaxTooltipLength) text = text.Substring(0, MaxTooltipLength);
            if (_icon.Text != text) _icon.Text = text;
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

        /// <summary>The window's Apply: validate, write config.txt with its comments, restart capture.</summary>
        public void SaveSettings(IDictionary<string, string> values)
        {
            if (values == null) throw new ArgumentNullException("values");
            var text = Config.Text(values);
            var fresh = Config.Parse(text); // throws ConfigException with a message meant for the screen
            File.WriteAllText(_configPath, text, Encoding.UTF8);
            Log.Info("settings saved from the window");
            ApplyConfig(fresh);
        }

        /// <summary>Puts a new config live: a running recording is finished, the pipeline restarts (buffer starts empty), hotkeys re-register.</summary>
        public void ApplyConfig(Config fresh)
        {
            if (fresh == null) throw new ArgumentNullException("fresh");
            Log.Info("reloading config");
            _config = fresh;
            if (_session == null) return; // still getting ffmpeg: the new config is picked up when capture starts
            if (_session.Recording) StopRecording("config change");
            _session.Rebuild(fresh);
            RegisterHotkeys();
            StartVoice();
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
                    switch (e.Slot)
                    {
                        case HotkeyWindow.ClipSlot: SaveClip("hotkey", _config.Seconds); break;
                        case HotkeyWindow.ShortSlot: SaveClip("short hotkey", _config.ShortSeconds); break;
                        case HotkeyWindow.RecordSlot: ToggleRecording("hotkey"); break;
                        case HotkeyWindow.ScreenshotSlot: TakeScreenshot("hotkey"); break;
                    }
                };
            }

            _saveItem.Text = "Save clip now  (" + Register(HotkeyWindow.ClipSlot, HotkeySpec.Parse(_config.Hotkey)) + ")";
            _saveShortItem.Text = "Save last " + _config.ShortSeconds + " s" + Optional(HotkeyWindow.ShortSlot, _config.HotkeyShort);
            _screenshotItem.Text = "Screenshot" + Optional(HotkeyWindow.ScreenshotSlot, _config.HotkeyScreenshot);
            RefreshRecordItem();
            FillSaveLastMenu();
        }

        /// <summary>"  (Ctrl+Alt+O)" for a hotkey that is set and free; nothing for one set to off.</summary>
        private string Optional(int slot, string hotkey)
        {
            if (hotkey.Length == 0)
            {
                _hotkeyWindow.Unregister(slot);
                return "";
            }
            return "  (" + Register(slot, HotkeySpec.Parse(hotkey)) + ")";
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

        private void RefreshRecordItem()
        {
            if (_recordItem == null) return;
            var key = _config.HotkeyRecord.Length > 0 ? "  (" + HotkeySpec.Parse(_config.HotkeyRecord).Text + ")" : "";
            _recordItem.Text = (Recording ? "Stop recording" : "Start recording") + key;
        }

        /// <summary>Medal's per-length clip buttons, as a submenu: 15 s, 30 s, 60 s… up to the buffer.</summary>
        private void FillSaveLastMenu()
        {
            if (_saveLastMenu == null) return;
            _saveLastMenu.DropDownItems.Clear();
            foreach (var length in new[] { 5, 10, 15, 30, 60, 120, 300 })
            {
                if (length >= _config.Seconds) break;
                var seconds = length;
                _saveLastMenu.DropDownItems.Add(new ToolStripMenuItem(Length(seconds), null, (s, e) => SaveClip("menu " + seconds + " s", seconds)));
            }
            var full = _config.Seconds;
            _saveLastMenu.DropDownItems.Add(new ToolStripMenuItem(Length(full) + " (everything)", null, (s, e) => SaveClip("menu", full)));
        }

        private static string Length(int seconds)
        {
            return seconds % 60 == 0 && seconds >= 60 ? seconds / 60 + " min" : seconds + " s";
        }

        private void StartSignals()
        {
            bool created;
            _saveEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.SaveEventName, out created);
            _saveShortEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.SaveShortEventName, out created);
            _recordEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.RecordEventName, out created);
            _screenshotEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ScreenshotEventName, out created);
            _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEventName, out created);
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName, out created);
            var signals = new WaitHandle[] { _saveEvent, _saveShortEvent, _recordEvent, _screenshotEvent, _showEvent, _quitEvent };
            _signalThread = new Thread(() =>
            {
                while (!_disposed)
                {
                    var fired = WaitHandle.WaitAny(signals, 500);
                    if (_disposed) break;
                    if (fired == 0) SaveClip("--save", _config.Seconds);
                    else if (fired == 1) SaveClip("--save-short", _config.ShortSeconds);
                    else if (fired == 2) OnUi(() => ToggleRecording("--record"));
                    else if (fired == 3) TakeScreenshot("--screenshot");
                    else if (fired == 4) OnUi(ShowWindow);
                    else if (fired == 5)
                    {
                        Log.Info("quit from --quit");
                        OnUi(Quit);
                        break;
                    }
                }
            }) { IsBackground = true, Name = "rewind-signals" };
            _signalThread.Start();
        }

        // ---- voice ("clip that") ----

        /// <summary>Starts (or restarts, after a config change) the phrase listener on a background thread; off = stopped.</summary>
        private void StartVoice()
        {
            var old = _voice;
            _voice = null;
            if (old != null) old.Dispose();
            if (!_config.VoiceClip) return;
            var config = _config;
            Action<string, float> onHeard = (heard, confidence) => SaveClip("voice \"" + heard + "\"", _config.Seconds);
            IVoiceTrigger voice = config.VoiceEngine == Config.VoiceRioVoice
                ? (IVoiceTrigger)new RioVoice(config.VoicePhrase, new Uri(config.VoiceUrl), onHeard)
                : new WindowsVoice(config.VoicePhrase, onHeard);
            _voice = voice;
            var worker = new Thread(() =>
            {
                if (!voice.TryStart())
                    OnUi(() => Balloon("Voice clipping isn't listening", voice.Status + " Rewind keeps trying every 15 s.", ToolTipIcon.Warning));
            }) { IsBackground = true, Name = "rewind-voice-start" };
            worker.Start();
        }

        // ---- saving ----

        public void SaveClip(string reason, int seconds)
        {
            var config = _config;
            if (_session == null)
            {
                OnUi(() => Balloon("Not ready yet", "Rewind is still getting ffmpeg; recording starts when that's done.", ToolTipIcon.Warning));
                return;
            }
            var result = _session.SaveAsync(seconds, reason,
                clip => OnUi(() =>
                {
                    var title = seconds == config.Seconds ? "Clip saved" : "Clip saved (" + seconds + " s)";
                    Notify(Chime.Clip, title, Path.GetFileName(clip.Path), () => ShowInFolder(clip.Path));
                    Prune();
                }),
                error => OnUi(() =>
                {
                    Sounds.Play(Chime.Failed, config, _appDir);
                    Balloon("Clip NOT saved", error.Message + "\nClick to open the log.", ToolTipIcon.Error, () => OpenFile(Log.Path));
                }));

            if (result == SaveStart.Paused)
            {
                var why = _session.PauseReason.Length > 0 ? _session.PauseReason : "not running";
                Log.Info("save (" + reason + ") ignored: " + why);
                OnUi(() => Balloon("Not recording", "Rewind is " + why + ", so there's nothing to save.", ToolTipIcon.Warning));
            }
            else if (result == SaveStart.Busy)
            {
                OnUi(() => Balloon("Hold on", "Still saving the last clip.", ToolTipIcon.Info));
            }
        }

        // ---- screenshots ----

        public void TakeScreenshot(string reason)
        {
            var config = _config;
            if (_session == null)
            {
                OnUi(() => Balloon("Not ready yet", "Rewind is still getting ffmpeg.", ToolTipIcon.Warning));
                return;
            }
            var result = _session.ScreenshotAsync(reason,
                path => OnUi(() =>
                {
                    var copied = CopyImageToClipboard(path);
                    Notify(Chime.Screenshot, "Screenshot saved", Path.GetFileName(path) + (copied ? "  ·  copied, paste it anywhere" : ""), () => ShowInFolder(path));
                    RaiseFilesChanged();
                    Prune();
                }),
                error => OnUi(() =>
                {
                    Sounds.Play(Chime.Failed, config, _appDir);
                    Balloon("Screenshot NOT saved", error.Message + "\nClick to open the log.", ToolTipIcon.Error, () => OpenFile(Log.Path));
                }));
            if (result == SaveStart.Paused)
            {
                var why = _session.PauseReason.Length > 0 ? _session.PauseReason : "not running";
                Log.Info("screenshot (" + reason + ") ignored: " + why);
                OnUi(() => Balloon("Not recording", "Rewind is " + why + ", so there's no picture to save.", ToolTipIcon.Warning));
            }
            else if (result == SaveStart.Busy)
            {
                OnUi(() => Balloon("Hold on", "Still saving the last screenshot.", ToolTipIcon.Info));
            }
        }

        /// <summary>The PNG onto the clipboard so Ctrl+V pastes it into Discord. False (logged) if the clipboard is busy.</summary>
        private static bool CopyImageToClipboard(string path)
        {
            try
            {
                using (var image = Image.FromFile(path)) Clipboard.SetImage(image);
                return true;
            }
            catch (ExternalException error)
            {
                Log.Warn("screenshot not copied to the clipboard: " + error.Message);
            }
            catch (OutOfMemoryException error)
            {
                Log.Warn("screenshot not copied to the clipboard: " + error.Message);
            }
            catch (IOException error)
            {
                Log.Warn("screenshot not copied to the clipboard: " + error.Message);
            }
            return false;
        }

        // ---- long recording ----

        public void ToggleRecording(string reason)
        {
            if (_session == null)
            {
                Balloon("Not ready yet", "Rewind is still getting ffmpeg.", ToolTipIcon.Warning);
                return;
            }
            if (_session.Recording)
            {
                StopRecording(reason);
                return;
            }
            try
            {
                _session.StartRecording();
            }
            catch (InvalidOperationException error)
            {
                Log.Info("recording (" + reason + ") not started: " + error.Message);
                Balloon("Not recording", error.Message, ToolTipIcon.Warning);
                return;
            }
            catch (IOException error)
            {
                Log.Error("recording (" + reason + ") not started: " + error.Message);
                Sounds.Play(Chime.Failed, _config, _appDir);
                Balloon("Recording NOT started", error.Message, ToolTipIcon.Error);
                return;
            }
            Log.Info("recording started (" + reason + ")");
            var key = _config.HotkeyRecord.Length > 0 ? HotkeySpec.Parse(_config.HotkeyRecord).Text : "the tray menu";
            Notify(Chime.RecordStart, "Recording", "Press " + key + " again to stop. Stops itself after " + _config.RecordingMaxMinutes + " min.", null);
            RefreshRecordItem();
            RefreshTooltip();
            ShowRecordingPill();
        }

        private void StopRecording(string reason)
        {
            if (_session == null || _stoppingRecording) return;
            _stoppingRecording = true;
            Log.Info("recording stopping (" + reason + ")");
            var stopped = _session.StopRecording(result => OnUi(() => RecordingFinished(result)));
            if (!stopped) _stoppingRecording = false;
            if (_config.Toast) Toast.Unpin();
            RefreshRecordItem();
            RefreshTooltip();
            if (stopped) Balloon("Finishing the recording", "Wrapping it up as an MP4; a moment.", ToolTipIcon.Info);
        }

        private void RecordingFinished(RecordingResult result)
        {
            _stoppingRecording = false;
            if (_disposed) return;
            RefreshRecordItem();
            RefreshTooltip();
            RaiseFilesChanged();
            if (result.Files.Count == 0)
            {
                Sounds.Play(Chime.Failed, _config, _appDir);
                Balloon("Recording NOT saved", (result.Error.Length > 0 ? result.Error : "Nothing was captured.") + "\nClick to open the log.", ToolTipIcon.Error, () => OpenFile(Log.Path));
                return;
            }
            var last = result.Files[result.Files.Count - 1];
            var length = string.Format("{0}:{1:00}", (int)result.Length.TotalMinutes, result.Length.Seconds);
            var parts = result.Files.Count > 1 ? " in " + result.Files.Count + " files" : "";
            Notify(Chime.RecordStop, "Recording saved  ·  " + length + parts, Path.GetFileName(last), () => ShowInFolder(last));
            if (result.Error.Length > 0) Balloon("Part of the recording had a problem", result.Error, ToolTipIcon.Warning);
            Prune();
        }

        private void ShowRecordingPill()
        {
            if (!_config.Toast || _session == null || !_session.Recording) return;
            var status = _session.Status;
            var elapsed = status.Recording ?? TimeSpan.Zero;
            Toast.Pin("REC  " + string.Format("{0}:{1:00}", (int)elapsed.TotalMinutes, elapsed.Seconds),
                string.Format("{0:0} MB so far  ·  {1} clips still work", status.RecordingMb, HotkeySpec.Parse(_config.Hotkey).Text));
        }

        // ---- the window ----

        private void ShowWindow()
        {
            if (_disposed) return;
            if (_window == null || _window.IsDisposed) _window = new ClipsForm(this, _appDir);
            if (!_window.Visible) _window.Show();
            if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
            _window.Activate();
        }

        public void PlayTestSound()
        {
            Sounds.Play(Chime.Clip, _config, _appDir);
        }

        private void RaiseFilesChanged()
        {
            var handler = FilesChanged;
            if (handler != null) handler();
        }

        // ---- storage cap ----

        /// <summary>After anything lands: drop the oldest clips until the folder fits max_storage_gb. Off the UI thread; one at a time.</summary>
        private void Prune()
        {
            var config = _config;
            if (config.MaxStorageGb == 0) return;
            if (Interlocked.CompareExchange(ref _pruning, 1, 0) != 0) return;
            var worker = new Thread(() =>
            {
                var deleted = 0;
                try
                {
                    var clips = ClipLibrary.Scan(config.ClipsFolder);
                    foreach (var clip in StoragePolicy.ToDelete(clips, config.MaxStorageGb * StoragePolicy.BytesPerGb))
                    {
                        try
                        {
                            File.Delete(clip.Path);
                            deleted++;
                            Log.Info(string.Format("storage cap {0} GB: deleted {1} ({2:0.0} MB)", config.MaxStorageGb, clip.FileName, clip.Bytes / 1048576.0));
                        }
                        catch (IOException error) { Log.Warn("storage cap: couldn't delete " + clip.FileName + ": " + error.Message); }
                        catch (UnauthorizedAccessException error) { Log.Warn("storage cap: couldn't delete " + clip.FileName + ": " + error.Message); }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _pruning, 0);
                }
                if (deleted > 0) OnUi(RaiseFilesChanged);
            }) { IsBackground = true, Name = "rewind-prune" };
            worker.Start();
        }

        // ---- recordings a crash left behind ----

        private void RecoverRecordings()
        {
            var config = _config;
            var ffmpeg = _ffmpeg;
            var labels = _session.AudioLabels;
            var worker = new Thread(() =>
            {
                var finished = SessionRecording.Recover(config.ClipsFolder, ffmpeg, labels, config.Codec);
                if (finished.Count == 0) return;
                OnUi(() =>
                {
                    var last = finished[finished.Count - 1];
                    Balloon("Recording recovered", finished.Count + " recording(s) from last time finished as MP4. Click to see.", ToolTipIcon.Info, () => ShowInFolder(last));
                    RaiseFilesChanged();
                });
            }) { IsBackground = true, Name = "rewind-recover" };
            worker.Start();
        }

        // ---- game watch + audio retry (every 2 s / 15 s on the UI thread) ----

        private void Tick()
        {
            if (_disposed) return;
            WatchGame();
            if (_session != null && _session.Recording)
            {
                var elapsed = _session.Status.Recording ?? TimeSpan.Zero;
                if (elapsed > TimeSpan.FromMinutes(_config.RecordingMaxMinutes))
                {
                    Log.Info("recording hit recording_max_minutes (" + _config.RecordingMaxMinutes + ")");
                    StopRecording("time limit");
                }
                else ShowRecordingPill();
            }
            RefreshTooltip();
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
            if (info.ProcessName != _lastInFront)
            {
                // Only in games mode, only on a change: a line per app that comes to the front.
                _lastInFront = info.ProcessName;
                Log.Info("in front: " + GameDetector.Describe(info, _config.Games));
            }
            if (game && _session.GamePaused)
            {
                Log.Info("game in front (" + info.ProcessName + "): recording");
                _session.SetGamePaused(false);
            }
            else if (!game && !_session.GamePaused && now - _lastGameSeenUtc > TimeSpan.FromSeconds(_config.GameGraceSeconds))
            {
                Log.Info("no game in front for " + _config.GameGraceSeconds + " s: waiting for one");
                if (_session.Recording) StopRecording("game closed");
                _session.SetGamePaused(true);
            }
        }

        /// <summary>Every 15 s: an audio device that was missing, and the voice listener if it lost its mic.</summary>
        private void ProbeMissing()
        {
            if (_disposed || _probing) return;
            var voice = _voice;
            var retryVoice = voice != null && !voice.Listening;
            if (_session.MissingTaps.Count == 0 && !retryVoice) return;
            _probing = true;
            var session = _session;
            var worker = new Thread(() =>
            {
                TapSpec found = null;
                try
                {
                    if (session.MissingTaps.Count > 0) found = session.ProbeMissing();
                    if (retryVoice && ReferenceEquals(voice, _voice)) voice.TryStart();
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
                    ApplyConfig(_config);
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
            _recordingIcon = DrawIcon(Color.FromArgb(230, 40, 40), false);
            _longRecordingIcon = DrawIcon(Color.FromArgb(230, 40, 40), true);
            _pausedIcon = DrawIcon(Color.FromArgb(120, 120, 120), false);
            _waitingIcon = DrawIcon(Color.FromArgb(235, 160, 30), false);

            var menu = new ContextMenuStrip();
            _saveItem = new ToolStripMenuItem("Save clip now", null, (s, e) => SaveClip("menu", _config.Seconds));
            _saveItem.Font = new Font(_saveItem.Font, FontStyle.Bold);
            _saveShortItem = new ToolStripMenuItem("Save short clip", null, (s, e) => SaveClip("menu", _config.ShortSeconds));
            _saveLastMenu = new ToolStripMenuItem("Save last…");
            _recordItem = new ToolStripMenuItem("Start recording", null, (s, e) => ToggleRecording("menu"));
            _screenshotItem = new ToolStripMenuItem("Screenshot", null, (s, e) => TakeScreenshot("menu"));
            _pauseItem = new ToolStripMenuItem("Pause recording", null, (s, e) => TogglePause());
            menu.Items.Add(new ToolStripMenuItem("Open Rewind  (clips + settings)", null, (s, e) => ShowWindow()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_saveItem);
            menu.Items.Add(_saveShortItem);
            menu.Items.Add(_saveLastMenu);
            menu.Items.Add(_screenshotItem);
            menu.Items.Add(_recordItem);
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

        /// <summary>The red dot with a replay arrow; the long-recording one carries a white square (a stop button) instead.</summary>
        private static Icon DrawIcon(Color fill, bool longRecording)
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, 3, 3, 26, 26);
                if (longRecording)
                {
                    using (var brush = new SolidBrush(Color.White)) g.FillRectangle(brush, 11, 11, 10, 10);
                }
                else
                {
                    using (var pen = new Pen(Color.FromArgb(255, 255, 255), 2.5f)) g.DrawArc(pen, 9, 9, 14, 14, 20, 300);
                    using (var brush = new SolidBrush(Color.White)) g.FillPolygon(brush, new[] { new Point(20, 6), new Point(26, 12), new Point(18, 13) });
                }
                using (var icon = Icon.FromHandle(bitmap.GetHicon())) return (Icon)icon.Clone();
            }
        }

        private void RefreshTooltip()
        {
            if (_icon == null || _session == null) return;
            var status = _session.Status;
            var text = status.Recording.HasValue
                ? string.Format("Rewind: REC {0}:{1:00} ({2:0} MB), {3:0} s buffered", (int)status.Recording.Value.TotalMinutes, status.Recording.Value.Seconds, status.RecordingMb, status.BufferedSeconds)
                : string.Format("Rewind: {0}, {1:0} s buffered ({2:0} MB)", status.Text, status.BufferedSeconds, status.BufferedMb);
            if (text.Length > MaxTooltipLength) text = text.Substring(0, MaxTooltipLength);
            if (_icon.Text != text) _icon.Text = text;
            var wanted = status.PauseReason == "waiting for a game" ? _waitingIcon
                : status.Paused ? _pausedIcon
                : status.Recording.HasValue ? _longRecordingIcon : _recordingIcon;
            if (_icon.Icon != wanted) _icon.Icon = wanted;
        }

        public void TogglePause()
        {
            if (_session == null) return;
            var paused = !_session.UserPaused;
            if (paused && _session.Recording) StopRecording("paused");
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
                if (_session.Recording) OnUi(() => StopRecording("locked"));
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

        /// <summary>Something good happened: the chime, then the on-screen card (toast=on) or a balloon. UI thread.</summary>
        private void Notify(Chime chime, string title, string text, Action onClick)
        {
            Sounds.Play(chime, _config, _appDir);
            if (_config.Toast)
            {
                try
                {
                    Toast.Flash(title, text, onClick);
                    return;
                }
                catch (Exception error)
                {
                    Log.Warn("toast failed, falling back to a balloon: " + error.Message);
                }
            }
            Balloon(title, text + (onClick != null ? "\nClick to show it in the folder." : ""), ToolTipIcon.Info, onClick);
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
            FinishRecordingBeforeQuit();
            Dispose();
            Application.Exit();
        }

        /// <summary>A recording mustn't be lost to a quit: finish it (up to two minutes for a huge one) before the pipeline goes.</summary>
        private void FinishRecordingBeforeQuit()
        {
            if (_session == null || !_session.Recording) return;
            var done = new ManualResetEvent(false);
            RecordingResult outcome = null;
            _session.StopRecording(result => { outcome = result; done.Set(); });
            if (_config.Toast) Toast.Unpin();
            Log.Info("quit: finishing the recording first");
            var waited = 0;
            while (!done.WaitOne(100) && waited < StopRecordingWaitMs)
            {
                Application.DoEvents();
                waited += 100;
            }
            if (outcome == null) Log.Warn("quit: the recording didn't finish in time; its .ts will be recovered next start");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            if (_tickTimer != null) _tickTimer.Dispose();
            if (_probeTimer != null) _probeTimer.Dispose();
            if (_voice != null) _voice.Dispose();
            if (_hotkeyWindow != null) _hotkeyWindow.Dispose();
            Toast.CloseAll();
            if (_window != null)
            {
                _window.AllowClose();
                _window.Dispose();
            }
            if (_session != null) _session.Dispose();
            if (_saveEvent != null) _saveEvent.Dispose();
            if (_saveShortEvent != null) _saveShortEvent.Dispose();
            if (_recordEvent != null) _recordEvent.Dispose();
            if (_screenshotEvent != null) _screenshotEvent.Dispose();
            if (_quitEvent != null) _quitEvent.Dispose();
            if (_showEvent != null) _showEvent.Dispose();
            if (_icon != null)
            {
                _icon.Visible = false;
                _icon.Dispose();
            }
            if (_ui != null) _ui.Dispose();
        }
    }
}
