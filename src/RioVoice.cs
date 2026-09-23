using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Rewind
{
    /// <summary>
    /// "Clip that" through Rio's own speech-to-text (RioVoice, Nemotron on the homelab). The mic
    /// is read straight from WASAPI; a loudness gate (VoiceActivity) notices when someone starts
    /// talking, and only then is a WebSocket opened and the speech streamed as 16 kHz int16 PCM
    /// (with a little pre-roll so the first syllable isn't lost). Every reply carries the text so
    /// far; the phrase is checked on each one, so the clip fires while the sentence is still
    /// going. Silence ends the stream. Room tone is never sent, so the ASR sits idle otherwise.
    /// </summary>
    internal sealed class RioVoice : IVoiceTrigger
    {
        private const int SliceMs = 100;
        private const int PreRollSlices = 4;          // 400 ms before the gate opened
        private const int HangSlices = 9;             // 900 ms of quiet ends an utterance
        private const int MaxUtteranceSlices = 60;    // 6 s: the phrase is short; long talk restarts a fresh stream
        private const int SendEveryMs = 300;
        private const int ConnectTimeoutMs = 6000;
        private const int ReplyTimeoutMs = 8000;
        private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan TriggerCooldown = TimeSpan.FromSeconds(3);
        private const int ShareModeShared = 0;
        private const uint BufferFlagsSilent = 0x2;
        private const long BufferDurationHns = 2000000;
        private const int PollMs = 10;

        private readonly string _phrase;
        private readonly Uri _url;
        private readonly Action<string, float> _onHeard;
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);
        private volatile string _status = "off";
        private volatile string _openError;
        private volatile bool _disposed;
        private Thread _captureThread;
        private Utterance _current;
        private DateTime _lastFailureUtc = DateTime.MinValue;
        private DateTime _lastTriggerUtc = DateTime.MinValue;
        private long _utterances, _streamed, _failures;

        public RioVoice(string phrase, Uri url, Action<string, float> onHeard)
        {
            if (string.IsNullOrEmpty(phrase)) throw new ArgumentException("phrase");
            if (url == null) throw new ArgumentNullException("url");
            if (onHeard == null) throw new ArgumentNullException("onHeard");
            _phrase = phrase;
            _url = url;
            _onHeard = onHeard;
        }

        public string Status { get { return _status; } }
        public bool Listening { get { return _status == "listening"; } }

        /// <summary>For the log: utterances the gate caught, how many were streamed, how many streams failed.</summary>
        public string Stats
        {
            get
            {
                return string.Format("voice (riovoice): {0} utterances, {1} streamed, {2} failed", Interlocked.Read(ref _utterances), Interlocked.Read(ref _streamed), Interlocked.Read(ref _failures));
            }
        }

        public bool TryStart()
        {
            Stop();
            _disposed = false;
            _openError = null;
            _ready.Reset();
            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "rewind-riovoice-mic" };
            _captureThread.SetApartmentState(ApartmentState.MTA);
            _captureThread.Start();
            if (!_ready.WaitOne(5000))
            {
                _status = "the mic took too long to open";
                Log.Warn("voice (riovoice): not listening: " + _status);
                return false;
            }
            if (_openError != null)
            {
                _status = _openError;
                Log.Warn("voice (riovoice): not listening: " + _status);
                return false;
            }
            _status = "listening";
            Log.Info(string.Format("voice (riovoice): listening for \"{0}\" via {1}", _phrase, _url));
            return true;
        }

        public void Stop()
        {
            _disposed = true;
            var utterance = _current;
            _current = null;
            if (utterance != null) utterance.Abort();
            var thread = _captureThread;
            _captureThread = null;
            if (thread != null && thread != Thread.CurrentThread) thread.Join(1500);
            if (_status == "listening") _status = "off";
        }

        public void Dispose()
        {
            Stop();
        }

        // ---- the mic ----

        private void CaptureLoop()
        {
            try
            {
                RunCapture();
            }
            catch (Exception error)
            {
                if (_ready.WaitOne(0) == false)
                {
                    _openError = error.Message;
                    _ready.Set();
                    return;
                }
                _status = "the mic stopped: " + error.Message;
                Log.Warn("voice (riovoice): " + _status + "; will retry");
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
                AudioTap.Check(enumerator.GetDefaultAudioEndpoint(CoreAudio.DataFlowCapture, CoreAudio.RoleMultimedia, out device), "no default microphone");
                object clientObject;
                var clientId = typeof(IAudioClient).GUID;
                AudioTap.Check(device.Activate(ref clientId, CoreAudio.ClsCtxAll, IntPtr.Zero, out clientObject), "Activate");
                client = (IAudioClient)clientObject;
                AudioTap.Check(client.GetMixFormat(out mixFormat), "GetMixFormat");
                var format = AudioTap.ReadFormat(mixFormat);
                AudioTap.Check(client.Initialize(ShareModeShared, 0, BufferDurationHns, 0, mixFormat, IntPtr.Zero), "Initialize");
                object captureObject;
                var captureId = typeof(IAudioCaptureClient).GUID;
                AudioTap.Check(client.GetService(ref captureId, out captureObject), "GetService");
                capture = (IAudioCaptureClient)captureObject;
                AudioTap.Check(client.Start(), "Start");
                started = true;
                _ready.Set();
                Pump(capture, format);
            }
            finally
            {
                if (started && client != null) { try { client.Stop(); } catch (COMException) { } }
                if (mixFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormat);
                AudioTap.ReleaseSafely(capture);
                AudioTap.ReleaseSafely(client);
                AudioTap.ReleaseSafely(device);
                AudioTap.ReleaseSafely(enumerator);
            }
        }

        /// <summary>Reads packets, cuts them into 100 ms slices, runs the gate, hands speech to the current utterance.</summary>
        private void Pump(IAudioCaptureClient capture, PcmFormat format)
        {
            var gate = new VoiceActivity(HangSlices, MaxUtteranceSlices);
            var sliceFrames = format.SampleRate * SliceMs / 1000;
            var slice = new List<float>(sliceFrames);
            var preRoll = new Queue<byte[]>();
            var lastStats = DateTime.UtcNow;
            while (!_disposed)
            {
                uint packetFrames;
                AudioTap.Check(capture.GetNextPacketSize(out packetFrames), "GetNextPacketSize");
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
                    AudioTap.Check(capture.GetBuffer(out data, out frames, out flags, out devicePosition, out counterPosition), "GetBuffer");
                    try
                    {
                        if (frames > 0)
                        {
                            var bytes = new byte[(int)frames * format.BytesPerFrame];
                            if ((flags & BufferFlagsSilent) == 0) Marshal.Copy(data, bytes, 0, bytes.Length);
                            slice.AddRange(SpeechGate.ToMono(bytes, bytes.Length, format));
                        }
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }
                    while (slice.Count >= sliceFrames)
                    {
                        var mono = slice.GetRange(0, sliceFrames).ToArray();
                        slice.RemoveRange(0, sliceFrames);
                        OnSlice(gate, mono, format.SampleRate, preRoll);
                    }
                    AudioTap.Check(capture.GetNextPacketSize(out packetFrames), "GetNextPacketSize");
                }
                if (DateTime.UtcNow - lastStats > TimeSpan.FromMinutes(10))
                {
                    lastStats = DateTime.UtcNow;
                    Log.Info(string.Format(CultureInfoSafe(), "{0}, room level {1:0.0000}", Stats, gate.RoomLevel));
                }
            }
        }

        private static IFormatProvider CultureInfoSafe()
        {
            return System.Globalization.CultureInfo.InvariantCulture;
        }

        private void OnSlice(VoiceActivity gate, float[] mono, int rate, Queue<byte[]> preRoll)
        {
            var pcm = SpeechGate.ToPcm16k(mono, rate);
            var decision = gate.Feed(SpeechGate.Rms(mono));
            switch (decision)
            {
                case GateEvent.Quiet:
                    preRoll.Enqueue(pcm);
                    while (preRoll.Count > PreRollSlices) preRoll.Dequeue();
                    return;
                case GateEvent.Started:
                    Interlocked.Increment(ref _utterances);
                    if (DateTime.UtcNow - _lastFailureUtc < FailureBackoff) return; // the server was unhappy a moment ago: let it be
                    var utterance = new Utterance(this);
                    _current = utterance;
                    foreach (var earlier in preRoll) utterance.Add(earlier);
                    preRoll.Clear();
                    utterance.Add(pcm);
                    utterance.Start();
                    return;
                case GateEvent.Speaking:
                    if (_current != null) _current.Add(pcm);
                    return;
                case GateEvent.Ended:
                    var done = _current;
                    _current = null;
                    if (done != null) { done.Add(pcm); done.End(); }
                    return;
            }
        }

        /// <summary>Called from an utterance's thread with the text so far. True when the phrase is in it (once per cooldown).</summary>
        private bool Check(string transcript, ref int consumed)
        {
            int upTo;
            if (!SpeechGate.Heard(transcript, _phrase, consumed, out upTo)) return false;
            consumed = upTo;
            if (DateTime.UtcNow - _lastTriggerUtc < TriggerCooldown) return true;
            _lastTriggerUtc = DateTime.UtcNow;
            Log.Info("voice (riovoice): \"" + transcript.Trim() + "\"");
            _onHeard(transcript.Trim(), 1f);
            return true;
        }

        private void Failed(string what)
        {
            Interlocked.Increment(ref _failures);
            _lastFailureUtc = DateTime.UtcNow;
            Log.Warn("voice (riovoice): " + what + "; pausing voice streams for 30 s");
        }

        /// <summary>One stretch of speech: its own WebSocket, fed from a queue on its own thread, closed after the final reply.</summary>
        private sealed class Utterance
        {
            private readonly RioVoice _owner;
            private readonly object _gate = new object();
            private readonly Queue<byte[]> _pending = new Queue<byte[]>();
            private readonly MemoryStream _batch = new MemoryStream();
            private bool _ended;
            private volatile bool _aborted;
            private Thread _thread;

            public Utterance(RioVoice owner)
            {
                _owner = owner;
            }

            public void Add(byte[] pcm)
            {
                lock (_gate)
                {
                    if (_ended) return;
                    _pending.Enqueue(pcm);
                    Monitor.Pulse(_gate);
                }
            }

            public void End()
            {
                lock (_gate)
                {
                    _ended = true;
                    Monitor.Pulse(_gate);
                }
            }

            public void Abort()
            {
                _aborted = true;
                End();
            }

            public void Start()
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "rewind-riovoice-stream" };
                _thread.Start();
            }

            private void Run()
            {
                var consumed = 0;
                var cts = new CancellationTokenSource();
                using (var ws = new ClientWebSocket())
                {
                    try
                    {
                        var connect = ws.ConnectAsync(_owner._url, cts.Token);
                        if (!connect.Wait(ConnectTimeoutMs)) throw new TimeoutException("connecting took over " + ConnectTimeoutMs / 1000 + " s");
                        var reply = new byte[64 * 1024];
                        var sentAny = false;
                        var lastSend = DateTime.UtcNow;
                        while (!_aborted)
                        {
                            byte[] next = null;
                            bool ended;
                            lock (_gate)
                            {
                                while (_pending.Count == 0 && !_ended) Monitor.Wait(_gate, 200);
                                if (_pending.Count > 0) next = _pending.Dequeue();
                                ended = _ended && _pending.Count == 0;
                            }
                            if (next != null) _batch.Write(next, 0, next.Length);
                            var due = _batch.Length > 0 && (DateTime.UtcNow - lastSend).TotalMilliseconds >= SendEveryMs;
                            if (due || (ended && _batch.Length > 0))
                            {
                                var frame = _batch.ToArray();
                                _batch.SetLength(0);
                                lastSend = DateTime.UtcNow;
                                Send(ws, frame, WebSocketMessageType.Binary, cts.Token);
                                sentAny = true;
                                var text = Receive(ws, reply, cts.Token);
                                if (text == null) break;
                                var heard = SpeechGate.ParseText(text);
                                if (heard != null && _owner.Check(heard, ref consumed)) { Finish(ws, reply, cts.Token, sentAny); return; }
                                if (SpeechGate.IsFinal(text)) break; // the server capped the stream
                            }
                            if (ended) { Finish(ws, reply, cts.Token, sentAny); return; }
                        }
                        Close(ws, cts.Token);
                    }
                    catch (AggregateException error)
                    {
                        _owner.Failed("stream failed: " + (error.InnerException ?? error).Message);
                    }
                    catch (WebSocketException error) { _owner.Failed("stream failed: " + error.Message); }
                    catch (TimeoutException error) { _owner.Failed("stream failed: " + error.Message); }
                    catch (InvalidOperationException error) { _owner.Failed("stream failed: " + error.Message); }
                    finally
                    {
                        cts.Dispose();
                    }
                }
            }

            /// <summary>Tells the server the utterance is over, reads the final (two-pass) text, checks it, closes.</summary>
            private void Finish(ClientWebSocket ws, byte[] reply, CancellationToken token, bool sentAny)
            {
                if (sentAny)
                {
                    Interlocked.Increment(ref _owner._streamed);
                    Send(ws, Encoding.UTF8.GetBytes("{\"type\":\"eos\"}"), WebSocketMessageType.Text, token);
                    var text = Receive(ws, reply, token);
                    var heard = text != null ? SpeechGate.ParseText(text) : null;
                    if (!string.IsNullOrEmpty(heard))
                    {
                        // What was said is nobody's business: only the phrase is acted on, nothing else is logged.
                        var consumed = 0;
                        _owner.Check(heard, ref consumed);
                    }
                }
                Close(ws, token);
            }

            private static void Send(ClientWebSocket ws, byte[] data, WebSocketMessageType type, CancellationToken token)
            {
                if (!ws.SendAsync(new ArraySegment<byte>(data), type, true, token).Wait(ReplyTimeoutMs))
                    throw new TimeoutException("the server stopped taking audio");
            }

            /// <summary>One whole text message, or null when the server closed.</summary>
            private static string Receive(ClientWebSocket ws, byte[] buffer, CancellationToken token)
            {
                var total = 0;
                while (true)
                {
                    var task = ws.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), token);
                    if (!task.Wait(ReplyTimeoutMs)) throw new TimeoutException("no reply from the server in " + ReplyTimeoutMs / 1000 + " s");
                    var result = task.Result;
                    if (result.MessageType == WebSocketMessageType.Close) return null;
                    total += result.Count;
                    if (result.EndOfMessage || total >= buffer.Length) return Encoding.UTF8.GetString(buffer, 0, total);
                }
            }

            private static void Close(ClientWebSocket ws, CancellationToken token)
            {
                try
                {
                    if (ws.State == WebSocketState.Open) ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", token).Wait(2000);
                }
                catch (AggregateException) { }
                catch (WebSocketException) { }
                catch (ObjectDisposedException) { }
            }
        }
    }
}
