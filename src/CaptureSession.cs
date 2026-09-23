using System;
using System.Collections.Generic;
using System.Threading;

namespace Rewind
{
    /// <summary>One audio tap Rewind wants, as the config describes it. Immutable.</summary>
    internal sealed class TapSpec
    {
        public readonly string Label;
        public readonly string PipeName;
        public readonly bool Loopback;
        public readonly string Filter;

        public TapSpec(string label, string pipeName, bool loopback, string filter)
        {
            if (string.IsNullOrEmpty(label)) throw new ArgumentException("label");
            if (string.IsNullOrEmpty(pipeName)) throw new ArgumentException("pipeName");
            Label = label;
            PipeName = pipeName;
            Loopback = loopback;
            Filter = filter ?? "";
        }

        public static IList<TapSpec> From(Config config)
        {
            if (config == null) throw new ArgumentNullException("config");
            var specs = new List<TapSpec>();
            if (config.GameAudio) specs.Add(new TapSpec("Game", "rewind_game", true, ""));
            if (config.Mic) specs.Add(new TapSpec("Mic", "rewind_mic", false, config.MicFilter));
            return specs.AsReadOnly();
        }

        public AudioTap NewTap()
        {
            return new AudioTap(Label, PipeName, Loopback, Filter);
        }
    }

    internal enum SaveStart { Started, Paused, Busy }

    /// <summary>A snapshot of what the session is doing, for the tooltip and the window. Immutable.</summary>
    internal sealed class SessionStatus
    {
        public readonly string Text;
        public readonly bool Paused;
        /// <summary>"" while recording; otherwise "paused", "paused while locked" or "waiting for a game".</summary>
        public readonly string PauseReason;
        public readonly double BufferedSeconds;
        public readonly double BufferedMb;
        public readonly IList<string> MissingAudio;
        /// <summary>How long the long recording has run; null when there is none.</summary>
        public readonly TimeSpan? Recording;
        public readonly double RecordingMb;

        public SessionStatus(string text, bool paused, string pauseReason, double bufferedSeconds, double bufferedMb, IList<string> missingAudio,
            TimeSpan? recording, double recordingMb)
        {
            Text = text;
            Paused = paused;
            PauseReason = pauseReason;
            BufferedSeconds = bufferedSeconds;
            BufferedMb = bufferedMb;
            MissingAudio = missingAudio;
            Recording = recording;
            RecordingMb = recordingMb;
        }
    }

    /// <summary>
    /// The capture pipeline as one thing: the ring, the audio taps, the recorder that keeps ffmpeg
    /// alive, and the three reasons recording can be paused (the user, the lock screen, no game in
    /// front). Rebuild() swaps in a new config; ProbeMissing() checks whether an audio device that
    /// wasn't there at start has turned up. Clips, screenshots and long recordings all read the
    /// same ring.
    /// </summary>
    internal sealed class CaptureSession : IDisposable
    {
        private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);
        private static readonly IList<TapSpec> NoSpecs = new List<TapSpec>().AsReadOnly();
        private static readonly IList<AudioTap> NoTaps = new List<AudioTap>().AsReadOnly();

        private readonly string _ffmpeg;
        private Config _config;
        private ChunkRing _ring;
        private IList<AudioTap> _taps = NoTaps;
        private IList<TapSpec> _missing = NoSpecs;
        private Recorder _recorder;
        private SessionRecording _recording;
        private bool _userPaused;
        private bool _lockPaused;
        private bool _gamePaused;
        private int _saving;
        private int _shooting;

        /// <summary>An audio device that couldn't be opened: the spec and a plain-English reason.</summary>
        public event Action<TapSpec, string> AudioMissing;
        /// <summary>Raised on the save thread after every clip.</summary>
        public event Action<SavedClip> ClipSaved;

        public CaptureSession(Config config, string ffmpegPath)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (string.IsNullOrEmpty(ffmpegPath)) throw new ArgumentException("ffmpegPath");
            _config = config;
            _ffmpeg = ffmpegPath;
        }

        public Config Config { get { return _config; } }
        public IList<TapSpec> MissingTaps { get { return _missing; } }
        public bool UserPaused { get { return _userPaused; } }
        public bool LockPaused { get { return _lockPaused; } }
        public bool GamePaused { get { return _gamePaused; } }
        public bool Saving { get { return _saving != 0; } }
        public bool Recording { get { return _recording != null; } }
        public SessionRecording CurrentRecording { get { return _recording; } }

        /// <summary>The audio track names in the order they are in the stream.</summary>
        public IList<string> AudioLabels
        {
            get
            {
                var labels = new List<string>();
                foreach (var tap in _taps) labels.Add(tap.Label);
                return labels.AsReadOnly();
            }
        }

        public void Start()
        {
            if (_recorder != null) throw new InvalidOperationException("Session already started.");
            Open();
        }

        /// <summary>Tears the pipeline down and brings it back up with a new config. The buffer starts empty; a running recording is finished first.</summary>
        public void Rebuild(Config config)
        {
            if (config == null) throw new ArgumentNullException("config");
            Close();
            _config = config;
            Open();
        }

        public void SetUserPaused(bool paused) { _userPaused = paused; ApplyPause(); }
        public void SetLockPaused(bool paused) { _lockPaused = paused; ApplyPause(); }
        public void SetGamePaused(bool paused) { _gamePaused = paused; ApplyPause(); }

        public SessionStatus Status
        {
            get
            {
                var recorder = _recorder;
                var ring = _ring;
                var recording = _recording;
                var reason = PauseReason;
                var missing = new List<string>();
                foreach (var spec in _missing) missing.Add(spec.Label);
                var text = reason.Length > 0
                    ? char.ToUpperInvariant(reason[0]) + reason.Substring(1)
                    : recorder != null ? recorder.Status : "Stopped";
                return new SessionStatus(text, reason.Length > 0, reason,
                    ring != null ? ring.Span.TotalSeconds : 0, ring != null ? ring.Bytes / 1048576.0 : 0, missing.AsReadOnly(),
                    recording != null ? recording.Elapsed : (TimeSpan?)null, recording != null ? recording.Bytes / 1048576.0 : 0);
            }
        }

        /// <summary>Why recording is off right now, or empty.</summary>
        public string PauseReason
        {
            get
            {
                if (_userPaused) return "paused";
                if (_lockPaused) return "paused while locked";
                if (_gamePaused) return "waiting for a game";
                return "";
            }
        }

        /// <summary>
        /// Saves the last N seconds on a background thread. onSaved / onFailed run on that thread.
        /// Returns Paused or Busy (and calls nothing) when a save can't start.
        /// </summary>
        public SaveStart SaveAsync(int seconds, string reason, Action<SavedClip> onSaved, Action<Exception> onFailed)
        {
            if (onSaved == null) throw new ArgumentNullException("onSaved");
            if (onFailed == null) throw new ArgumentNullException("onFailed");
            var recorder = _recorder;
            if (recorder == null || recorder.Paused) return SaveStart.Paused;
            if (Interlocked.CompareExchange(ref _saving, 1, 0) != 0) return SaveStart.Busy;

            var labels = AudioLabels;
            var config = _config;
            var ring = _ring;
            var ffmpeg = _ffmpeg;
            var worker = new Thread(() =>
            {
                try
                {
                    var clip = ClipSaver.Save(ring, config, ffmpeg, labels, seconds);
                    Log.Info(string.Format("clip saved ({0}, {1} s): {2} ({3:0.0} MB, buffer held {4:0} s, {5})",
                        reason, seconds, clip.Path, clip.Bytes / 1048576.0, clip.Buffered.TotalSeconds,
                        clip.CleanCut ? "clean cut at keyframe, " + clip.TrimmedBytes / 1024 + " KB trimmed" : "raw cut, no keyframe found"));
                    var handler = ClipSaved;
                    if (handler != null) handler(clip);
                    onSaved(clip);
                }
                catch (Exception error)
                {
                    Log.Error("clip save failed: " + error);
                    onFailed(error);
                }
                finally
                {
                    Interlocked.Exchange(ref _saving, 0);
                }
            }) { IsBackground = true, Name = "rewind-save" };
            worker.Start();
            return SaveStart.Started;
        }

        /// <summary>Saves the newest frame as a PNG on a background thread; the callbacks run there with the path.</summary>
        public SaveStart ScreenshotAsync(string reason, Action<string> onSaved, Action<Exception> onFailed)
        {
            if (onSaved == null) throw new ArgumentNullException("onSaved");
            if (onFailed == null) throw new ArgumentNullException("onFailed");
            var recorder = _recorder;
            if (recorder == null || recorder.Paused) return SaveStart.Paused;
            if (Interlocked.CompareExchange(ref _shooting, 1, 0) != 0) return SaveStart.Busy;

            var config = _config;
            var ring = _ring;
            var ffmpeg = _ffmpeg;
            var worker = new Thread(() =>
            {
                try
                {
                    var path = Screenshot.Take(ring, config, ffmpeg);
                    Log.Info(string.Format("screenshot saved ({0}): {1}", reason, path));
                    onSaved(path);
                }
                catch (Exception error)
                {
                    Log.Error("screenshot failed: " + error);
                    onFailed(error);
                }
                finally
                {
                    Interlocked.Exchange(ref _shooting, 0);
                }
            }) { IsBackground = true, Name = "rewind-screenshot" };
            worker.Start();
            return SaveStart.Started;
        }

        /// <summary>Begins a long recording from the ring's newest keyframe. Throws InvalidOperationException when paused or already recording.</summary>
        public void StartRecording()
        {
            var recorder = _recorder;
            if (recorder == null || recorder.Paused) throw new InvalidOperationException("Rewind is " + (PauseReason.Length > 0 ? PauseReason : "not running") + ", so there's nothing to record.");
            if (_recording != null) throw new InvalidOperationException("Already recording.");
            var recording = new SessionRecording(_ring, _config, _ffmpeg, AudioLabels);
            recording.Start();
            _recording = recording;
        }

        /// <summary>Ends the long recording; onFinished runs on a background thread once the MP4s exist. False when none was running.</summary>
        public bool StopRecording(Action<RecordingResult> onFinished)
        {
            if (onFinished == null) throw new ArgumentNullException("onFinished");
            var recording = _recording;
            _recording = null;
            if (recording == null) return false;
            recording.Stop(onFinished);
            return true;
        }

        /// <summary>
        /// Tries each missing audio device once (a few seconds at most; call off the UI thread).
        /// Returns the first that opens, closed again so Rebuild can take it properly, or null.
        /// </summary>
        public TapSpec ProbeMissing()
        {
            foreach (var spec in _missing)
            {
                var tap = spec.NewTap();
                try
                {
                    tap.Open(OpenTimeout);
                    return spec;
                }
                catch (InvalidOperationException)
                {
                    // Still not there.
                }
                finally
                {
                    tap.Dispose();
                }
            }
            return null;
        }

        public void Dispose()
        {
            Close();
        }

        // ---- pipeline ----

        private void Open()
        {
            _ring = new ChunkRing(TimeSpan.FromSeconds(_config.Seconds + 2));
            var taps = new List<AudioTap>();
            var missing = new List<TapSpec>();
            foreach (var spec in TapSpec.From(_config))
            {
                var tap = spec.NewTap();
                try
                {
                    tap.Open(OpenTimeout);
                    taps.Add(tap);
                }
                catch (InvalidOperationException error)
                {
                    tap.Dispose();
                    missing.Add(spec);
                    Log.Warn(error.Message);
                    var handler = AudioMissing;
                    if (handler != null) handler(spec, error.Message);
                }
            }
            _taps = taps.AsReadOnly();
            _missing = missing.AsReadOnly();
            _recorder = new Recorder(_config, _ring, _ffmpeg, _taps);
            if (WantPaused) _recorder.Pause(); // paused before it starts: no ffmpeg is launched
            _recorder.Start();
        }

        private void Close()
        {
            // A recording nobody stopped (a config change, a quit): finish it quietly so the file is playable.
            StopRecording(result => { });
            var recorder = _recorder;
            _recorder = null;
            if (recorder != null) recorder.Dispose();
            foreach (var tap in _taps) tap.Dispose();
            _taps = NoTaps;
            _missing = NoSpecs;
        }

        private bool WantPaused { get { return _userPaused || _lockPaused || _gamePaused; } }

        private void ApplyPause()
        {
            var recorder = _recorder;
            if (recorder == null) return;
            if (WantPaused) recorder.Pause(); else recorder.Resume();
        }
    }
}
