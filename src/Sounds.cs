using System;
using System.Collections.Generic;
using System.IO;
using System.Media;

namespace Rewind
{
    internal enum Chime { Clip, Screenshot, RecordStart, RecordStop, Failed }

    /// <summary>
    /// Rewind's own sounds. Windows' "Asterisk" event is whatever the sound scheme says (on some
    /// PCs a near-silent whoosh) and goes through the System Sounds mixer channel, so nobody
    /// hears it in a game. These chimes are made in code (a few sine notes with a decay), played
    /// through Rewind's own audio session at the volume in config.txt. A .wav next to the exe
    /// with the chime's name (clip.wav, screenshot.wav, record-start.wav, record-stop.wav,
    /// failed.wav) replaces the built-in one.
    /// </summary>
    internal static class Sounds
    {
        public const int SampleRate = 48000;
        private const double MaxAmplitude = 0.8;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, byte[]> Cache = new Dictionary<string, byte[]>();
        private static SoundPlayer _player;

        /// <summary>One note: sine (or noise) starting at startMs, dying off with the decay.</summary>
        private sealed class Tone
        {
            public readonly double Freq;
            public readonly int StartMs, LengthMs;
            public readonly double Decay;
            public readonly bool Noise;

            public Tone(double freq, int startMs, int lengthMs, double decay, bool noise)
            {
                Freq = freq; StartMs = startMs; LengthMs = lengthMs; Decay = decay; Noise = noise;
            }
        }

        /// <summary>The file name a custom replacement would have next to the exe.</summary>
        public static string FileName(Chime chime)
        {
            switch (chime)
            {
                case Chime.Screenshot: return "screenshot.wav";
                case Chime.RecordStart: return "record-start.wav";
                case Chime.RecordStop: return "record-stop.wav";
                case Chime.Failed: return "failed.wav";
                default: return "clip.wav";
            }
        }

        /// <summary>Plays the chime (custom file if present). Never throws: a sound that can't play is logged, not fatal.</summary>
        public static void Play(Chime chime, Config config, string appDir)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (!config.Sound || config.SoundVolume == 0) return;
            try
            {
                var custom = appDir != null ? Path.Combine(appDir, FileName(chime)) : null;
                lock (Gate)
                {
                    if (_player != null) _player.Dispose();
                    _player = custom != null && File.Exists(custom)
                        ? new SoundPlayer(custom)
                        : new SoundPlayer(new MemoryStream(Wav(chime, config.SoundVolume)));
                    _player.Play();
                }
            }
            catch (Exception error)
            {
                Log.Warn("couldn't play the " + chime + " sound: " + error.Message);
            }
        }

        /// <summary>A complete WAV file (16-bit mono) for the chime at the volume (0-100). Pure; cached.</summary>
        public static byte[] Wav(Chime chime, int volume)
        {
            if (volume < 0 || volume > 100) throw new ArgumentOutOfRangeException("volume");
            var key = chime + "|" + volume;
            lock (Gate)
            {
                byte[] cached;
                if (Cache.TryGetValue(key, out cached)) return cached;
            }

            var tones = Tones(chime);
            var totalMs = 60;
            foreach (var tone in tones) totalMs = Math.Max(totalMs, tone.StartMs + tone.LengthMs + 40);
            var samples = SampleRate * totalMs / 1000;
            var mix = new double[samples];
            var random = new Random(7);
            foreach (var tone in tones)
            {
                var from = SampleRate * tone.StartMs / 1000;
                var count = SampleRate * tone.LengthMs / 1000;
                for (var i = 0; i < count && from + i < samples; i++)
                {
                    var t = i / (double)SampleRate;
                    var envelope = Math.Exp(-t * tone.Decay) * Math.Min(1.0, i / (SampleRate * 0.003)); // 3 ms fade-in: no click
                    var value = tone.Noise ? random.NextDouble() * 2 - 1 : Math.Sin(2 * Math.PI * tone.Freq * t);
                    mix[from + i] += value * envelope;
                }
            }
            var peak = 0.0;
            foreach (var value in mix) peak = Math.Max(peak, Math.Abs(value));
            var gain = peak > 0 ? MaxAmplitude * (volume / 100.0) / peak : 0;

            var bytes = new byte[44 + samples * 2];
            WriteHeader(bytes, samples);
            for (var i = 0; i < samples; i++)
            {
                var sample = (short)Math.Round(Math.Max(-1.0, Math.Min(1.0, mix[i] * gain)) * short.MaxValue);
                bytes[44 + i * 2] = (byte)(sample & 0xff);
                bytes[45 + i * 2] = (byte)((sample >> 8) & 0xff);
            }
            lock (Gate) Cache[key] = bytes;
            return bytes;
        }

        private static Tone[] Tones(Chime chime)
        {
            switch (chime)
            {
                case Chime.Screenshot: // a camera: a short click of noise and a tick
                    return new[] { new Tone(0, 0, 25, 90, true), new Tone(2400, 20, 60, 60, false) };
                case Chime.RecordStart: // one note
                    return new[] { new Tone(660, 0, 260, 9, false) };
                case Chime.RecordStop: // two notes down
                    return new[] { new Tone(1320, 0, 160, 12, false), new Tone(880, 130, 280, 8, false) };
                case Chime.Failed: // a low buzz
                    return new[] { new Tone(220, 0, 220, 6, false), new Tone(233, 0, 220, 6, false) };
                default: // clip: two notes up, the second held
                    return new[] { new Tone(880, 0, 150, 14, false), new Tone(1320, 110, 320, 8, false) };
            }
        }

        private static void WriteHeader(byte[] bytes, int samples)
        {
            var dataBytes = samples * 2;
            Ascii(bytes, 0, "RIFF");
            Int32(bytes, 4, 36 + dataBytes);
            Ascii(bytes, 8, "WAVE");
            Ascii(bytes, 12, "fmt ");
            Int32(bytes, 16, 16);
            Int16(bytes, 20, 1);            // PCM
            Int16(bytes, 22, 1);            // mono
            Int32(bytes, 24, SampleRate);
            Int32(bytes, 28, SampleRate * 2); // byte rate
            Int16(bytes, 32, 2);            // block align
            Int16(bytes, 34, 16);           // bits
            Ascii(bytes, 36, "data");
            Int32(bytes, 40, dataBytes);
        }

        private static void Ascii(byte[] bytes, int at, string text)
        {
            for (var i = 0; i < text.Length; i++) bytes[at + i] = (byte)text[i];
        }

        private static void Int32(byte[] bytes, int at, int value)
        {
            bytes[at] = (byte)value; bytes[at + 1] = (byte)(value >> 8); bytes[at + 2] = (byte)(value >> 16); bytes[at + 3] = (byte)(value >> 24);
        }

        private static void Int16(byte[] bytes, int at, int value)
        {
            bytes[at] = (byte)value; bytes[at + 1] = (byte)(value >> 8);
        }
    }
}
