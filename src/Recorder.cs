using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Rewind
{
    /// <summary>
    /// Keeps exactly one ffmpeg capture alive and pours its output into the ring. When ffmpeg dies
    /// (a monitor re-plug, the lock screen, a driver hiccup) it comes back on its own, waiting 2 s,
    /// then 4, 8, up to 15 s between tries so a permanent fault can't spin.
    /// </summary>
    internal sealed class Recorder : IDisposable
    {
        private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan HealthySession = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FormatCheckEvery = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan HealthLogEvery = TimeSpan.FromMinutes(1);
        private const int ReadBufferBytes = 64 * 1024;
        private const int StderrTailLines = 8;

        private readonly Config _config;
        private readonly ChunkRing _ring;
        private readonly string _ffmpeg;
        private readonly IList<AudioTap> _taps;
        private readonly KillOnCloseJob _job;
        private readonly object _gate = new object();
        private Thread _thread;
        private Process _process;
        private volatile bool _stopping;
        private volatile bool _paused;
        private volatile bool _restartWanted;
        private volatile string _status = "Starting";
        private volatile string _lastError;

        public Recorder(Config config, ChunkRing ring, string ffmpegPath, IList<AudioTap> taps)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (ring == null) throw new ArgumentNullException("ring");
            if (string.IsNullOrEmpty(ffmpegPath)) throw new ArgumentException("ffmpegPath");
            if (taps == null) throw new ArgumentNullException("taps");
            _config = config;
            _ring = ring;
            _ffmpeg = ffmpegPath;
            _taps = taps;
            try
            {
                _job = new KillOnCloseJob();
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                // Not fatal: ffmpeg still gets killed on a normal quit, just not on a crash.
                Log.Warn("no kill-on-close job for ffmpeg: " + error.Message);
                _job = null;
            }
        }

        /// <summary>Short human text for the tray tooltip: Recording / Paused / Restarting in N s.</summary>
        public string Status { get { return _status; } }
        public string LastError { get { return _lastError; } }
        public bool Paused { get { return _paused; } }

        public void Start()
        {
            if (_thread != null) throw new InvalidOperationException("Recorder already started.");
            _thread = new Thread(Supervise) { IsBackground = true, Name = "rewind-supervisor" };
            _thread.Start();
        }

        public void Pause()
        {
            _paused = true;
            KillCurrent();
        }

        public void Resume()
        {
            _paused = false;
        }

        public void Restart()
        {
            _restartWanted = true;
            KillCurrent();
        }

        public void Dispose()
        {
            _stopping = true;
            KillCurrent();
            var thread = _thread;
            _thread = null;
            if (thread != null && thread != Thread.CurrentThread) thread.Join(3000);
            if (_job != null) _job.Dispose();
        }

        private void Supervise()
        {
            var backoff = MinBackoff;
            while (!_stopping)
            {
                if (_paused)
                {
                    _status = "Paused";
                    Thread.Sleep(200);
                    continue;
                }

                var started = DateTime.UtcNow;
                try
                {
                    _restartWanted = false;
                    RunSession();
                    if (_stopping) break;
                    if (_restartWanted || _paused)
                    {
                        backoff = MinBackoff;
                        continue;
                    }
                }
                catch (Exception error)
                {
                    _lastError = error.Message;
                    Log.Error("capture failed: " + error.Message);
                }
                if (_stopping) break;

                if (DateTime.UtcNow - started > HealthySession) backoff = MinBackoff;
                _status = string.Format("Restarting in {0:0} s", backoff.TotalSeconds);
                Log.Warn(_status + (_lastError != null ? " (" + _lastError + ")" : ""));
                SleepUnless(backoff);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
            _status = "Stopped";
        }

        private void RunSession()
        {
            var outputIndex = Dxgi.ResolveOutputIndex(_config.Monitor);
            var sources = new List<AudioSource>();
            foreach (var tap in _taps) sources.Add(tap.Source());
            var args = FfmpegArgs.Capture(_config, outputIndex, sources);
            Log.Info("ffmpeg " + args);

            // A fresh session is a fresh timeline: old chunks would splice onto it with a jump.
            _ring.Clear();
            foreach (var tap in _taps) tap.BeginSession();

            var process = new Process
            {
                StartInfo = new ProcessStartInfo(_ffmpeg, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // So a mic_filter can name a file next to the exe (rnnoise/voice.rnnn) however Rewind was started.
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                }
            };
            var stderrTail = new Queue<string>();
            Thread stderrThread = null;
            var exitCode = -1;
            try
            {
                process.Start();
                lock (_gate) _process = process;
                if (_job != null)
                {
                    try { _job.Add(process); }
                    catch (System.ComponentModel.Win32Exception error) { Log.Warn("ffmpeg not tied to Rewind's life: " + error.Message); }
                }
                stderrThread = new Thread(() => DrainStderr(process, stderrTail)) { IsBackground = true, Name = "rewind-ffmpeg-stderr" };
                stderrThread.Start();
                _status = "Recording";
                _lastError = null;

                var buffer = new byte[ReadBufferBytes];
                var stream = process.StandardOutput.BaseStream;
                var lastFormatCheck = DateTime.UtcNow;
                var lastHealthLog = DateTime.UtcNow;
                while (!_stopping)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break; // ffmpeg closed its output: it is on the way out
                    _ring.Add(buffer, read, DateTime.UtcNow);

                    if (DateTime.UtcNow - lastHealthLog > HealthLogEvery)
                    {
                        lastHealthLog = DateTime.UtcNow;
                        LogHealth();
                    }

                    if (DateTime.UtcNow - lastFormatCheck > FormatCheckEvery)
                    {
                        lastFormatCheck = DateTime.UtcNow;
                        if (AnyFormatChanged())
                        {
                            Log.Info("audio device format changed; restarting capture");
                            _restartWanted = true;
                            break;
                        }
                        if (CaptureFault.LooksAudioOnly(_ring.Bytes, _ring.Span, _config.BitrateMbps))
                        {
                            Log.Warn(string.Format("buffer holds {0:0} s in only {1:0} MB: the video has stopped; restarting capture",
                                _ring.Span.TotalSeconds, _ring.Bytes / 1048576.0));
                            _restartWanted = true;
                            break;
                        }
                    }
                }
            }
            finally
            {
                lock (_gate) _process = null;
                exitCode = Stop(process);
                foreach (var tap in _taps) tap.EndSession();
                if (stderrThread != null) stderrThread.Join(1000);
                process.Dispose();
            }

            if (!_stopping && !_restartWanted && !_paused)
            {
                string tail;
                lock (stderrTail) tail = string.Join(" | ", stderrTail.ToArray());
                throw new InvalidOperationException(string.Format("ffmpeg exited (code {0}){1}", exitCode,
                    tail.Length > 0 ? ": " + tail : ""));
            }
        }

        /// <summary>Once a minute: how full the ring is and whether each audio device is delivering.</summary>
        private void LogHealth()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("health: buffer {0:0} s / {1:0} MB", _ring.Span.TotalSeconds, _ring.Bytes / 1048576.0);
            foreach (var tap in _taps) sb.Append(" | ").Append(tap.Stats);
            Log.Info(sb.ToString());
        }

        private bool AnyFormatChanged()
        {
            foreach (var tap in _taps) if (tap.FormatChanged) return true;
            return false;
        }

        private void DrainStderr(Process process, Queue<string> tail)
        {
            try
            {
                string line;
                while ((line = process.StandardError.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    Log.Warn("ffmpeg: " + line);
                    lock (tail)
                    {
                        tail.Enqueue(line);
                        while (tail.Count > StderrTailLines) tail.Dequeue();
                    }
                    if (CaptureFault.IsVideoLost(line))
                    {
                        // ffmpeg would carry on with the audio pipes alone; end it so the supervisor starts a fresh grab.
                        Log.Warn("the screen grab ended; stopping ffmpeg so it restarts");
                        KillCurrent();
                    }
                }
            }
            catch (IOException)
            {
                // The pipe went away with the process: nothing more to read.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void KillCurrent()
        {
            Process process;
            lock (_gate) process = _process;
            if (process == null) return;
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already gone between the check and the kill.
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                Log.Warn("couldn't stop ffmpeg: " + error.Message);
            }
        }

        private static int Stop(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(2000);
                }
                return process.HasExited ? process.ExitCode : -1;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return -1;
            }
        }

        private void SleepUnless(TimeSpan duration)
        {
            var until = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < until && !_stopping && !_paused && !_restartWanted) Thread.Sleep(100);
        }
    }
}
