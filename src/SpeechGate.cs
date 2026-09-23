using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Rewind
{
    /// <summary>What the gate decided about one slice of audio.</summary>
    internal enum GateEvent { Quiet, Started, Speaking, Ended }

    /// <summary>
    /// The pure pieces of the RioVoice trigger, kept out of the audio and network code so they can
    /// be tested: turning the mic's float/int frames into the 16 kHz mono int16 PCM the ASR wants,
    /// a loudness gate that says when someone starts and stops talking (so the ASR only ever hears
    /// speech, not hours of room tone), and the phrase check on what comes back.
    /// </summary>
    internal static class SpeechGate
    {
        public const int TargetRate = 16000;

        /// <summary>Downmixes to mono and resamples to 16 kHz by linear interpolation; returns int16 little-endian bytes.</summary>
        public static byte[] ToPcm16k(float[] mono, int sourceRate)
        {
            if (mono == null) throw new ArgumentNullException("mono");
            if (sourceRate < 8000) throw new ArgumentOutOfRangeException("sourceRate");
            if (mono.Length == 0) return new byte[0];
            var count = (int)((long)mono.Length * TargetRate / sourceRate);
            var bytes = new byte[count * 2];
            var step = sourceRate / (double)TargetRate;
            for (var i = 0; i < count; i++)
            {
                var at = i * step;
                var index = (int)at;
                var next = Math.Min(mono.Length - 1, index + 1);
                var frac = at - index;
                var value = mono[index] * (1 - frac) + mono[next] * frac;
                var sample = (short)Math.Round(Math.Max(-1.0, Math.Min(1.0, value)) * short.MaxValue);
                bytes[i * 2] = (byte)(sample & 0xff);
                bytes[i * 2 + 1] = (byte)((sample >> 8) & 0xff);
            }
            return bytes;
        }

        /// <summary>Raw WASAPI frames (f32le / s16le / s32le, any channel count) to mono floats in -1..1.</summary>
        public static float[] ToMono(byte[] data, int count, PcmFormat format)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (format == null) throw new ArgumentNullException("format");
            if (count < 0 || count > data.Length) throw new ArgumentOutOfRangeException("count");
            var frames = count / format.BytesPerFrame;
            var mono = new float[frames];
            var bytesPerSample = format.BytesPerFrame / format.Channels;
            for (var f = 0; f < frames; f++)
            {
                var sum = 0.0;
                for (var c = 0; c < format.Channels; c++)
                {
                    var at = f * format.BytesPerFrame + c * bytesPerSample;
                    switch (format.SampleFormat)
                    {
                        case "f32le": sum += BitConverter.ToSingle(data, at); break;
                        case "s16le": sum += BitConverter.ToInt16(data, at) / 32768.0; break;
                        default: sum += BitConverter.ToInt32(data, at) / 2147483648.0; break;
                    }
                }
                mono[f] = (float)(sum / format.Channels);
            }
            return mono;
        }

        public static double Rms(float[] mono)
        {
            if (mono == null) throw new ArgumentNullException("mono");
            if (mono.Length == 0) return 0;
            var sum = 0.0;
            foreach (var v in mono) sum += v * v;
            return Math.Sqrt(sum / mono.Length);
        }

        /// <summary>
        /// "clip that" in what the ASR heard: case, punctuation and extra spaces don't matter, and
        /// the words must appear in order and next to each other. Only the text after `from` counts,
        /// so one utterance can't trigger twice as the cumulative transcript grows.
        /// </summary>
        public static bool Heard(string transcript, string phrase, int from, out int matchedUpTo)
        {
            matchedUpTo = from;
            if (string.IsNullOrEmpty(transcript) || string.IsNullOrEmpty(phrase)) return false;
            var wanted = Normalize(phrase);
            if (wanted.Length == 0) return false;
            var fresh = from < transcript.Length ? transcript.Substring(Math.Max(0, from)) : "";
            var text = Normalize(fresh);
            var at = (" " + text + " ").IndexOf(" " + wanted + " ", StringComparison.Ordinal);
            if (at < 0) return false;
            matchedUpTo = transcript.Length;
            return true;
        }

        /// <summary>Lower case, letters/digits/apostrophes only, single spaces.</summary>
        public static string Normalize(string text)
        {
            if (text == null) return "";
            var cleaned = Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9' ]+", " ");
            return Regex.Replace(cleaned, " +", " ").Trim();
        }

        private static readonly Regex TextField = new Regex("\"text\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
        private static readonly Regex Escape = new Regex("\\\\(u[0-9a-fA-F]{4}|.)", RegexOptions.CultureInvariant);

        /// <summary>The "text" value out of the ASR's {"text": "...", "final": bool} reply, unescaped; null when there is none.</summary>
        public static string ParseText(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var match = TextField.Match(json);
            if (!match.Success) return null;
            return Escape.Replace(match.Groups[1].Value, m =>
            {
                var code = m.Groups[1].Value;
                switch (code[0])
                {
                    case 'n': return "\n";
                    case 't': return "\t";
                    case 'r': return "\r";
                    case 'u': return ((char)Convert.ToInt32(code.Substring(1), 16)).ToString();
                    default: return code;
                }
            });
        }

        /// <summary>True for a reply that says the stream is over (final or capped).</summary>
        public static bool IsFinal(string json)
        {
            return !string.IsNullOrEmpty(json) && (json.Contains("\"final\": true") || json.Contains("\"final\":true"));
        }
    }

    /// <summary>
    /// Says when speech starts and stops from a stream of loudness readings. Speech starts when
    /// a slice is clearly louder than the room (a running estimate of the quiet level, times a
    /// factor, with a floor so a silent room doesn't trigger on nothing); it ends after a run of
    /// quiet slices, or when an utterance has gone on for the maximum. Pure state machine.
    /// </summary>
    internal sealed class VoiceActivity
    {
        public const double Floor = 0.008;         // about -42 dBFS: below this is never speech
        public const double StartFactor = 4.0;     // this many times the room level starts an utterance
        public const double StopFactor = 2.0;      // fall under this many times the room level to count as quiet
        private readonly int _hangSlices;
        private readonly int _maxSlices;
        private double _room = Floor;
        private int _quietRun;
        private int _spokenSlices;
        private bool _speaking;

        /// <param name="hangSlices">quiet slices in a row that end an utterance</param>
        /// <param name="maxSlices">longest utterance before it is cut</param>
        public VoiceActivity(int hangSlices, int maxSlices)
        {
            if (hangSlices < 1) throw new ArgumentOutOfRangeException("hangSlices");
            if (maxSlices < hangSlices) throw new ArgumentOutOfRangeException("maxSlices");
            _hangSlices = hangSlices;
            _maxSlices = maxSlices;
        }

        public bool Speaking { get { return _speaking; } }
        /// <summary>The running estimate of how loud the room is when nobody talks.</summary>
        public double RoomLevel { get { return _room; } }

        public GateEvent Feed(double rms)
        {
            if (rms < 0) throw new ArgumentOutOfRangeException("rms");
            var startAt = Math.Max(Floor, _room * StartFactor);
            var stopAt = Math.Max(Floor * 0.6, _room * StopFactor);
            if (!_speaking)
            {
                // Only quiet slices teach the room level, and they teach it slowly.
                _room = _room * 0.95 + Math.Min(rms, startAt) * 0.05;
                if (rms < startAt) return GateEvent.Quiet;
                _speaking = true;
                _quietRun = 0;
                _spokenSlices = 1;
                return GateEvent.Started;
            }
            _spokenSlices++;
            _quietRun = rms < stopAt ? _quietRun + 1 : 0;
            if (_quietRun >= _hangSlices || _spokenSlices >= _maxSlices)
            {
                _speaking = false;
                return GateEvent.Ended;
            }
            return GateEvent.Speaking;
        }
    }
}
