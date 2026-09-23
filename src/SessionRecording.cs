using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Rewind
{
    /// <summary>What a long recording left behind. Immutable.</summary>
    internal sealed class RecordingResult
    {
        /// <summary>The MP4s, oldest first: one per stretch of capture (a screen hiccup mid-recording starts a new one).</summary>
        public readonly IList<string> Files;
        public readonly TimeSpan Length;
        /// <summary>Empty when every part landed.</summary>
        public readonly string Error;

        public RecordingResult(IList<string> files, TimeSpan length, string error)
        {
            if (files == null) throw new ArgumentNullException("files");
            Files = files;
            Length = length;
            Error = error ?? "";
        }
    }

    /// <summary>
    /// Medal's "full session recording": from the moment the key is pressed until it is pressed
    /// again, every chunk that reaches the ring is also appended to a .ts file on disk, so the
    /// length is bounded by the disk and not by memory. It starts on the newest keyframe already
    /// in the ring (so it begins at most a second before the press) and, when the capture restarts
    /// underneath it, rolls to a new file (the new stream has its own clock). Stop() turns each
    /// .ts into an MP4 without re-encoding. If Rewind dies mid-way the .ts is still playable and
    /// Recover() finishes it on the next start.
    /// </summary>
    internal sealed class SessionRecording : IDisposable
    {
        public const string Suffix = "recording";
        private const int RemuxTimeoutMs = 600000;
        private static readonly TimeSpan Reach = TimeSpan.FromSeconds(3);

        private readonly object _gate = new object();
        private readonly ChunkRing _ring;
        private readonly Config _config;
        private readonly string _ffmpeg;
        private readonly IList<string> _audioLabels;
        private readonly List<string> _parts = new List<string>();
        private FileStream _stream;
        private long _bytes;
        private string _error;
        private bool _stopped;

        public readonly DateTime StartedUtc;
        public readonly string Game;

        public SessionRecording(ChunkRing ring, Config config, string ffmpegPath, IList<string> audioLabels)
        {
            if (ring == null) throw new ArgumentNullException("ring");
            if (config == null) throw new ArgumentNullException("config");
            if (string.IsNullOrEmpty(ffmpegPath)) throw new ArgumentException("ffmpegPath");
            if (audioLabels == null) throw new ArgumentNullException("audioLabels");
            _ring = ring;
            _config = config;
            _ffmpeg = ffmpegPath;
            _audioLabels = audioLabels;
            StartedUtc = DateTime.UtcNow;
            Game = ForegroundApp.Name();
        }

        public TimeSpan Elapsed { get { return DateTime.UtcNow - StartedUtc; } }
        public long Bytes { get { lock (_gate) return _bytes; } }
        public int Parts { get { lock (_gate) return _parts.Count; } }

        /// <summary>Opens the first file with the ring's newest keyframe and starts following the ring.</summary>
        public void Start()
        {
            var chunks = _ring.Chunks(Reach, DateTime.UtcNow);
            var plan = TsCut.PlanLast(chunks);
            lock (_gate)
            {
                if (_stream != null) throw new InvalidOperationException("Already recording.");
                OpenPart();
                if (plan.Clean)
                {
                    _stream.Write(plan.Prefix, 0, plan.Prefix.Length);
                    for (var i = plan.StartChunk; i < chunks.Count; i++)
                    {
                        var data = chunks[i].Data;
                        var offset = i == plan.StartChunk ? plan.StartOffset : 0;
                        _stream.Write(data, offset, data.Length - offset);
                        _bytes += data.Length - offset;
                    }
                }
            }
            _ring.Added += OnChunk;
            _ring.Cleared += OnCaptureRestarted;
            Log.Info("recording started" + (plan.Clean ? " from the newest keyframe" : " mid-stream (no keyframe in the ring yet)"));
        }

        /// <summary>Stops following the ring and, on a background thread, wraps every part in an MP4; onFinished runs there.</summary>
        public void Stop(Action<RecordingResult> onFinished)
        {
            if (onFinished == null) throw new ArgumentNullException("onFinished");
            List<string> parts;
            string error;
            var length = Elapsed;
            _ring.Added -= OnChunk;
            _ring.Cleared -= OnCaptureRestarted;
            lock (_gate)
            {
                if (_stopped) return;
                _stopped = true;
                ClosePart();
                parts = new List<string>(_parts);
                error = _error;
            }
            var worker = new Thread(() =>
            {
                var files = new List<string>();
                var problems = new StringBuilder();
                if (error != null) problems.Append(error);
                foreach (var ts in parts)
                {
                    try
                    {
                        var mp4 = Finish(ts, _ffmpeg, _audioLabels, _config.Codec);
                        if (mp4 != null) files.Add(mp4);
                    }
                    catch (Exception failure)
                    {
                        Log.Error("recording part not finished (" + ts + "): " + failure.Message);
                        if (problems.Length > 0) problems.Append(" ");
                        problems.Append(Path.GetFileName(ts)).Append(": ").Append(failure.Message);
                    }
                }
                onFinished(new RecordingResult(files.AsReadOnly(), length, problems.ToString()));
            }) { IsBackground = true, Name = "rewind-recording-finish" };
            worker.Start();
        }

        public void Dispose()
        {
            _ring.Added -= OnChunk;
            _ring.Cleared -= OnCaptureRestarted;
            lock (_gate)
            {
                _stopped = true;
                ClosePart();
            }
        }

        // ---- following the ring (the recorder's thread) ----

        private void OnChunk(Chunk chunk)
        {
            lock (_gate)
            {
                if (_stream == null) return;
                try
                {
                    _stream.Write(chunk.Data, 0, chunk.Data.Length);
                    _bytes += chunk.Data.Length;
                }
                catch (IOException failure)
                {
                    Fail(failure.Message);
                }
            }
        }

        /// <summary>The capture restarted: this file is complete as it is; the next chunk opens the next.</summary>
        private void OnCaptureRestarted()
        {
            lock (_gate)
            {
                if (_stream == null || _stopped) return;
                if (_bytes == 0) return; // nothing landed in this part yet: keep using it
                ClosePart();
                try
                {
                    OpenPart();
                    Log.Info("recording continues in a new file: the capture restarted");
                }
                catch (IOException failure)
                {
                    Fail(failure.Message);
                }
            }
        }

        private void OpenPart()
        {
            Directory.CreateDirectory(_config.ClipsFolder);
            var path = Path.Combine(_config.ClipsFolder, ClipLibrary.NewName(Game, DateTime.Now, Suffix, "ts"));
            for (var n = 2; File.Exists(path); n++)
                path = Path.Combine(_config.ClipsFolder, ClipLibrary.NewName(Game, DateTime.Now, Suffix + " " + n, "ts"));
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20);
            _parts.Add(path);
            _bytes = 0;
        }

        private void ClosePart()
        {
            var stream = _stream;
            _stream = null;
            if (stream == null) return;
            try { stream.Dispose(); }
            catch (IOException failure) { Fail(failure.Message); }
        }

        private void Fail(string message)
        {
            if (_error == null)
            {
                _error = "Recording stopped writing: " + message;
                Log.Error(_error);
            }
            ClosePart();
        }

        // ---- .ts -> .mp4 ----

        /// <summary>Wraps one .ts part in an MP4 beside it and removes the .ts. Null when the part is empty.</summary>
        public static string Finish(string tsPath, string ffmpegPath, IList<string> audioLabels, string codec)
        {
            if (string.IsNullOrEmpty(tsPath)) throw new ArgumentException("tsPath");
            var info = new FileInfo(tsPath);
            if (!info.Exists) return null;
            if (info.Length < TsCut.PacketSize * 3)
            {
                info.Delete();
                return null;
            }
            var mp4 = Path.ChangeExtension(tsPath, ".mp4");
            for (var n = 2; File.Exists(mp4); n++)
                mp4 = Path.Combine(Path.GetDirectoryName(tsPath) ?? "", Path.GetFileNameWithoutExtension(tsPath) + " " + n + ".mp4");
            var args = FfmpegArgs.RemuxFile(tsPath, mp4, audioLabels, codec);
            var start = new ProcessStartInfo(ffmpegPath, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            var stderr = new StringBuilder();
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("ffmpeg didn't start.");
                var drain = new Thread(() => ClipSaver.Collect(process, stderr)) { IsBackground = true, Name = "rewind-recording-stderr" };
                drain.Start();
                if (!process.WaitForExit(RemuxTimeoutMs))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw new InvalidOperationException("ffmpeg took over ten minutes to finish the recording; the .ts file is kept.");
                }
                drain.Join(1000);
                string text;
                lock (stderr) text = stderr.ToString().Trim();
                if (process.ExitCode != 0 || !File.Exists(mp4))
                {
                    ClipSaverCleanup(mp4);
                    throw new InvalidOperationException(string.Format("ffmpeg couldn't finish the recording (code {0}): {1}. The .ts file is kept.",
                        process.ExitCode, ClipSaver.Tail(text)));
                }
                if (text.Length > 0) Log.Warn("recording remux: " + text);
            }
            File.Delete(tsPath);
            Log.Info(string.Format("recording finished: {0} ({1:0.0} MB)", mp4, new FileInfo(mp4).Length / 1048576.0));
            return mp4;
        }

        private static void ClipSaverCleanup(string mp4)
        {
            try
            {
                var file = new FileInfo(mp4);
                if (file.Exists && file.Length == 0) file.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Recordings a crash cut short: every "… recording.ts" in the folder that nothing holds open. Finished one by one.</summary>
        public static IList<string> Recover(string folder, string ffmpegPath, IList<string> audioLabels, string codec)
        {
            var finished = new List<string>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return finished.AsReadOnly();
            string[] candidates;
            try { candidates = Directory.GetFiles(folder, "Rewind *" + Suffix + "*.ts"); }
            catch (IOException) { return finished.AsReadOnly(); }
            catch (UnauthorizedAccessException) { return finished.AsReadOnly(); }
            foreach (var ts in candidates)
            {
                try
                {
                    using (new FileStream(ts, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                    Log.Info("finishing a recording left behind: " + Path.GetFileName(ts));
                    var mp4 = Finish(ts, ffmpegPath, audioLabels, codec);
                    if (mp4 != null) finished.Add(mp4);
                }
                catch (IOException failure)
                {
                    Log.Warn("left " + Path.GetFileName(ts) + " alone: " + failure.Message);
                }
                catch (InvalidOperationException failure)
                {
                    Log.Warn("couldn't finish " + Path.GetFileName(ts) + ": " + failure.Message);
                }
            }
            return finished.AsReadOnly();
        }
    }
}
