using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;

namespace Rewind
{
    /// <summary>A slice of captured PCM and the moment it arrived. Never modified.</summary>
    internal sealed class AudioPacket
    {
        public readonly DateTime AtUtc;
        public readonly byte[] Data;

        public AudioPacket(DateTime atUtc, byte[] data)
        {
            AtUtc = atUtc;
            Data = data;
        }
    }

    /// <summary>What a device's shared-mode mix looks like. Immutable.</summary>
    internal sealed class PcmFormat
    {
        public readonly string SampleFormat;
        public readonly int SampleRate;
        public readonly int Channels;
        public readonly int BytesPerFrame;

        public PcmFormat(string sampleFormat, int sampleRate, int channels, int bytesPerFrame)
        {
            SampleFormat = sampleFormat;
            SampleRate = sampleRate;
            Channels = channels;
            BytesPerFrame = bytesPerFrame;
        }

        public bool SameAs(PcmFormat other)
        {
            return other != null && other.SampleFormat == SampleFormat && other.SampleRate == SampleRate
                && other.Channels == Channels && other.BytesPerFrame == BytesPerFrame;
        }

        public override string ToString()
        {
            return string.Format("{0} Hz, {1} ch, {2}", SampleRate, Channels, SampleFormat);
        }
    }

    /// <summary>
    /// One audio source for ffmpeg: either the mix going to the speakers (WASAPI loopback) or the
    /// microphone. Two threads:
    ///
    ///  * capture: pulls packets off WASAPI as they appear and queues them;
    ///  * writer:  feeds a named pipe at exactly the device's sample rate against wall-clock,
    ///             padding silence when nothing arrived (loopback goes quiet when no app is
    ///             playing) and dropping backlog if the queue runs ahead. That pacing is what keeps
    ///             the audio in step with the video: ffmpeg stamps raw PCM by sample count, so
    ///             sample count has to equal elapsed time.
    ///
    /// If Windows' default device changes, capture reopens on the new one. If the new device has
    /// a different format, FormatChanged goes true and the Recorder restarts ffmpeg.
    /// </summary>
    internal sealed class AudioTap : IDisposable
    {
        private const int PollMs = 10;
        private const int ShareModeShared = 0;
        private const uint StreamFlagsLoopback = 0x00020000;
        private const uint BufferFlagsSilent = 0x2;
        private const long BufferDurationHns = 2000000;   // 200 ms, in 100-nanosecond units
        private const double LatencySeconds = 0.06;       // the pipe runs this far behind real time
        private const double MaxBacklogSeconds = 0.4;     // more queued than this = drop the oldest
        private const double IdleKeepSeconds = 2.0;       // history kept while no session is running
        private const int MaxBytesPerWrite = 1 << 18;
        private static readonly TimeSpan DeviceCheckEvery = TimeSpan.FromSeconds(2);

        private static readonly Guid SubtypePcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
        private static readonly Guid SubtypeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");

        private readonly string _label;
        private readonly string _pipeName;
        private readonly bool _loopback;
        private readonly string _filter;

        private readonly object _gate = new object();
        private readonly Queue<AudioPacket> _queue = new Queue<AudioPacket>();
        private int _headOffset;   // bytes of the oldest queued packet already sent
        private long _queuedBytes;

        private long _packets;          // WASAPI packets received since open
        private long _silentPackets;    // of which Windows flagged as silence
        private long _paddedFrames;     // frames of silence invented because nothing had arrived

        private readonly ManualResetEvent _ready = new ManualResetEvent(false);
        private volatile string _openError;
        private volatile PcmFormat _format;
        private volatile bool _formatChanged;
        private volatile bool _disposed;
        private volatile bool _sessionOpen;
        private Thread _captureThread;
        private Thread _writerThread;
        private NamedPipeServerStream _server;

        public AudioTap(string label, string pipeName, bool loopback, string filter)
        {
            if (string.IsNullOrEmpty(label)) throw new ArgumentException("label");
            if (string.IsNullOrEmpty(pipeName)) throw new ArgumentException("pipeName");
            _label = label;
            _pipeName = pipeName;
            _loopback = loopback;
            _filter = filter ?? "";
        }

        public string Label { get { return _label; } }
        public PcmFormat Format { get { return _format; } }
        /// <summary>True once the device format no longer matches what ffmpeg was told.</summary>
        public bool FormatChanged { get { return _formatChanged; } }

        /// <summary>One line of health for the log: proves the device is delivering, silent or not.</summary>
        public string Stats
        {
            get
            {
                var format = _format;
                var rate = format != null ? format.SampleRate : 48000;
                return string.Format("{0}: {1} packets ({2} flagged silent), {3:0.0} s of silence padded",
                    _label, Interlocked.Read(ref _packets), Interlocked.Read(ref _silentPackets),
                    Interlocked.Read(ref _paddedFrames) / (double)rate);
            }
        }

        /// <summary>Starts capturing and waits until the device's format is known.</summary>
        public AudioSource Open(TimeSpan timeout)
        {
            if (_captureThread != null) throw new InvalidOperationException(_label + " audio is already open.");
            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "rewind-capture-" + _label };
            _captureThread.SetApartmentState(ApartmentState.MTA);
            _captureThread.Start();

            if (!_ready.WaitOne(timeout))
                throw new InvalidOperationException(_label + " audio: the device took too long to open.");
            if (_openError != null)
                throw new InvalidOperationException(_label + " audio: " + _openError);
            return Source();
        }

        public AudioSource Source()
        {
            var format = _format;
            if (format == null) throw new InvalidOperationException(_label + " audio isn't open.");
            return new AudioSource(_label, _pipeName, format.SampleFormat, format.SampleRate, format.Channels, _filter);
        }

        /// <summary>Creates the pipe ffmpeg will read from. Call right before launching ffmpeg.</summary>
        public void BeginSession()
        {
            EndSession();
            var format = _format;
            if (format == null) throw new InvalidOperationException(_label + " audio isn't open.");

            _formatChanged = false;
            var server = new NamedPipeServerStream(_pipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.None, 0, 1 << 20);
            _server = server;
            _sessionOpen = true;
            _writerThread = new Thread(() => WriterLoop(server, format))
            {
                IsBackground = true,
                Name = "rewind-pipe-" + _label
            };
            _writerThread.Start();
        }

        public void EndSession()
        {
            _sessionOpen = false;
            var server = _server;
            _server = null;
            if (server != null)
            {
                try { server.Dispose(); }
                catch (IOException) { /* already broken by ffmpeg exiting: nothing to clean up */ }
            }
            var thread = _writerThread;
            _writerThread = null;
            if (thread != null && thread != Thread.CurrentThread) thread.Join(1500);
        }

        public void Dispose()
        {
            _disposed = true;
            EndSession();
            var thread = _captureThread;
            _captureThread = null;
            // Bounded: the capture thread can be inside a COM call, and a tap that refuses to
            // exit is worse than one that leaves a background thread behind at shutdown.
            if (thread != null) thread.Join(1000);
        }

        // ---- writer: queue -> pipe, paced to wall-clock ----

        private void WriterLoop(NamedPipeServerStream server, PcmFormat format)
        {
            try
            {
                server.WaitForConnection();
                // Audio from before ffmpeg connected belongs to no recording: t = 0 is now.
                lock (_gate) DropAll();

                var clock = Stopwatch.StartNew();
                long sentFrames = 0;
                var maxFramesPerWrite = MaxBytesPerWrite / format.BytesPerFrame;
                var scratch = new byte[maxFramesPerWrite * format.BytesPerFrame];
                var maxBacklogBytes = (long)(MaxBacklogSeconds * format.SampleRate) * format.BytesPerFrame;

                while (_sessionOpen && !_disposed)
                {
                    var due = (long)((clock.Elapsed.TotalSeconds - LatencySeconds) * format.SampleRate);
                    var need = due - sentFrames;
                    if (need <= 0)
                    {
                        Thread.Sleep(PollMs / 2);
                        continue;
                    }

                    var frames = (int)Math.Min(need, maxFramesPerWrite);
                    var bytes = frames * format.BytesPerFrame;
                    int filled;
                    lock (_gate)
                    {
                        if (_formatChanged)
                        {
                            // What's queued is in a format ffmpeg wasn't told about. Send silence
                            // until the Recorder restarts the session with the new format.
                            DropAll();
                            filled = 0;
                        }
                        else
                        {
                            filled = Take(scratch, bytes);
                            TrimBacklog(maxBacklogBytes);
                        }
                    }
                    if (filled < bytes)
                    {
                        Array.Clear(scratch, filled, bytes - filled);
                        Interlocked.Add(ref _paddedFrames, (bytes - filled) / format.BytesPerFrame);
                    }
                    server.Write(scratch, 0, bytes);
                    sentFrames += frames;
                }
            }
            catch (IOException error)
            {
                if (_sessionOpen && !_disposed) Log.Warn(_label + " audio pipe closed: " + error.Message);
            }
            catch (ObjectDisposedException)
            {
                // EndSession closed the pipe under us: expected.
            }
            catch (Exception error)
            {
                Log.Error(_label + " audio writer died: " + error);
            }
        }

        private int Take(byte[] target, int bytes)
        {
            var filled = 0;
            while (filled < bytes && _queue.Count > 0)
            {
                var head = _queue.Peek();
                var available = head.Data.Length - _headOffset;
                var take = Math.Min(available, bytes - filled);
                Buffer.BlockCopy(head.Data, _headOffset, target, filled, take);
                filled += take;
                _headOffset += take;
                _queuedBytes -= take;
                if (_headOffset >= head.Data.Length)
                {
                    _queue.Dequeue();
                    _headOffset = 0;
                }
            }
            return filled;
        }

        private void TrimBacklog(long maxBytes)
        {
            while (_queuedBytes > maxBytes && _queue.Count > 0) DropHead();
        }

        private void DropHead()
        {
            var head = _queue.Dequeue();
            _queuedBytes -= head.Data.Length - _headOffset;
            _headOffset = 0;
        }

        private void DropAll()
        {
            _queue.Clear();
            _headOffset = 0;
            _queuedBytes = 0;
        }

        private void Enqueue(AudioPacket packet)
        {
            lock (_gate)
            {
                _queue.Enqueue(packet);
                _queuedBytes += packet.Data.Length;
                if (_sessionOpen) return;

                // Nobody is reading: keep a little history, not everything.
                var cutoff = packet.AtUtc - TimeSpan.FromSeconds(IdleKeepSeconds);
                while (_queue.Count > 0 && _queue.Peek().AtUtc < cutoff) DropHead();
            }
        }

        // ---- capture: WASAPI -> queue ----

        private void CaptureLoop()
        {
            while (!_disposed)
            {
                try
                {
                    RunCapture();
                }
                catch (Exception error)
                {
                    if (_format == null)
                    {
                        // Never got as far as a format: report it to Open() and give up.
                        _openError = error.Message;
                        _ready.Set();
                        return;
                    }
                    Log.Warn(_label + " audio capture stopped (" + error.Message + "); retrying in 1 s");
                    Thread.Sleep(1000);
                }
            }
        }

        private void RunCapture()
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            var mixFormat = IntPtr.Zero;
            var started = false;

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                var flow = _loopback ? CoreAudio.DataFlowRender : CoreAudio.DataFlowCapture;
                Check(enumerator.GetDefaultAudioEndpoint(flow, CoreAudio.RoleMultimedia, out device),
                    _loopback ? "no default output device" : "no default microphone");
                string deviceId;
                Check(device.GetId(out deviceId), "GetId");

                object clientObject;
                var clientId = typeof(IAudioClient).GUID;
                Check(device.Activate(ref clientId, CoreAudio.ClsCtxAll, IntPtr.Zero, out clientObject), "Activate");
                client = (IAudioClient)clientObject;

                Check(client.GetMixFormat(out mixFormat), "GetMixFormat");
                var format = ReadFormat(mixFormat);

                var previous = _format;
                if (previous == null)
                {
                    _format = format;
                    _ready.Set();
                }
                else if (!previous.SameAs(format))
                {
                    _format = format;
                    _formatChanged = true;
                    Log.Warn(_label + " audio: device format changed to " + format + "; recorder will restart");
                }

                Check(client.Initialize(ShareModeShared, _loopback ? StreamFlagsLoopback : 0u,
                    BufferDurationHns, 0, mixFormat, IntPtr.Zero), "Initialize");

                object captureObject;
                var captureId = typeof(IAudioCaptureClient).GUID;
                Check(client.GetService(ref captureId, out captureObject), "GetService");
                capture = (IAudioCaptureClient)captureObject;

                Check(client.Start(), "Start");
                started = true;
                Log.Info(_label + " audio open: " + format);

                Pump(enumerator, deviceId, capture, format);
            }
            finally
            {
                if (started && client != null) { try { client.Stop(); } catch (COMException) { } }
                if (mixFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormat);
                ReleaseSafely(capture);
                ReleaseSafely(client);
                ReleaseSafely(device);
                ReleaseSafely(enumerator);
            }
        }

        private void Pump(IMMDeviceEnumerator enumerator, string deviceId, IAudioCaptureClient capture, PcmFormat format)
        {
            var lastDeviceCheck = DateTime.UtcNow;
            while (!_disposed)
            {
                if (DateTime.UtcNow - lastDeviceCheck > DeviceCheckEvery)
                {
                    lastDeviceCheck = DateTime.UtcNow;
                    if (DefaultDeviceChanged(enumerator, deviceId))
                    {
                        Log.Info(_label + " audio: Windows' default device changed, reopening");
                        return;
                    }
                }

                uint packetFrames;
                Check(capture.GetNextPacketSize(out packetFrames), "GetNextPacketSize");
                if (packetFrames == 0)
                {
                    Thread.Sleep(PollMs);
                    continue;
                }

                while (packetFrames != 0 && !_disposed)
                {
                    IntPtr data;
                    uint frames, flags;
                    ulong devicePosition, counterPosition;
                    Check(capture.GetBuffer(out data, out frames, out flags, out devicePosition, out counterPosition), "GetBuffer");
                    try
                    {
                        if (frames > 0)
                        {
                            var bytes = new byte[(int)frames * format.BytesPerFrame];
                            // SILENT means "treat as zeros"; the pointer may hold anything.
                            var silent = (flags & BufferFlagsSilent) != 0;
                            if (!silent) Marshal.Copy(data, bytes, 0, bytes.Length);
                            Enqueue(new AudioPacket(DateTime.UtcNow, bytes));
                            Interlocked.Increment(ref _packets);
                            if (silent) Interlocked.Increment(ref _silentPackets);
                        }
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }
                    Check(capture.GetNextPacketSize(out packetFrames), "GetNextPacketSize");
                }
            }
        }

        private bool DefaultDeviceChanged(IMMDeviceEnumerator enumerator, string openedId)
        {
            IMMDevice current = null;
            try
            {
                var flow = _loopback ? CoreAudio.DataFlowRender : CoreAudio.DataFlowCapture;
                if (enumerator.GetDefaultAudioEndpoint(flow, CoreAudio.RoleMultimedia, out current) < 0) return true;
                string id;
                if (current.GetId(out id) < 0) return true;
                return !string.Equals(id, openedId, StringComparison.Ordinal);
            }
            finally
            {
                ReleaseSafely(current);
            }
        }

        /// <summary>Reads a WAVEFORMATEX(TENSIBLE) into the three facts ffmpeg needs. Shared with the RioVoice mic.</summary>
        internal static PcmFormat ReadFormat(IntPtr p)
        {
            var tag = (ushort)Marshal.ReadInt16(p, 0);
            var channels = (ushort)Marshal.ReadInt16(p, 2);
            var rate = Marshal.ReadInt32(p, 4);
            var blockAlign = (ushort)Marshal.ReadInt16(p, 12);
            var bits = (ushort)Marshal.ReadInt16(p, 14);

            bool isFloat;
            if (tag == 0xFFFE)
            {
                // The real format hides behind the extensible header: past WAVEFORMATEX's 18 bytes,
                // then wValidBitsPerSample (2) and dwChannelMask (4), sits the SubFormat GUID.
                var sub = new byte[16];
                Marshal.Copy(new IntPtr(p.ToInt64() + 24), sub, 0, 16);
                var subtype = new Guid(sub);
                if (subtype == SubtypeFloat) isFloat = true;
                else if (subtype == SubtypePcm) isFloat = false;
                else throw new InvalidOperationException("unsupported audio format (subtype " + subtype + ")");
            }
            else if (tag == 3) isFloat = true;
            else if (tag == 1) isFloat = false;
            else throw new InvalidOperationException("unsupported audio format (tag " + tag + ")");

            string sampleFormat;
            if (isFloat && bits == 32) sampleFormat = "f32le";
            else if (!isFloat && bits == 16) sampleFormat = "s16le";
            else if (!isFloat && bits == 32) sampleFormat = "s32le";
            else throw new InvalidOperationException(string.Format("unsupported audio format ({0}-bit {1})", bits, isFloat ? "float" : "int"));

            if (channels < 1 || rate < 8000 || blockAlign != channels * (bits / 8))
                throw new InvalidOperationException(string.Format("odd audio format ({0} ch, {1} Hz, block {2})", channels, rate, blockAlign));

            return new PcmFormat(sampleFormat, rate, channels, blockAlign);
        }

        internal static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what + " failed (0x" + hr.ToString("x8") + ")", hr);
        }

        internal static void ReleaseSafely(object comObject)
        {
            if (comObject == null) return;
            try { Marshal.ReleaseComObject(comObject); }
            catch (Exception) { /* a dead COM object at shutdown is not worth crashing over */ }
        }
    }
}
