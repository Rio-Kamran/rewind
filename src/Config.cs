using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Rewind
{
    /// <summary>A bad config.txt. The message is written to be shown to a person as-is.</summary>
    internal sealed class ConfigException : Exception
    {
        public ConfigException(string message) : base(message) { }
    }

    /// <summary>
    /// Everything Rewind can be told, read from config.txt. Immutable: Parse() builds a new one and
    /// nothing edits it afterwards; "Reload config" in the tray builds another.
    /// </summary>
    internal sealed class Config
    {
        public const int MinSeconds = 5, MaxSeconds = 600;
        public const int MinFps = 15, MaxFps = 240;
        public const int MinBitrate = 2, MaxBitrate = 150;
        public const int MaxAudioOffsetMs = 2000;

        private static readonly string[] KnownKeys =
        {
            "hotkey", "seconds", "fps", "bitrate_mbps", "codec", "monitor",
            "game_audio", "mic", "mic_filter", "audio_offset_ms", "clips", "ffmpeg"
        };

        public readonly string Hotkey;
        public readonly int Seconds;
        public readonly int Fps;
        public readonly int BitrateMbps;
        /// <summary>h264, hevc or av1: which NVENC encoder to use.</summary>
        public readonly string Codec;
        /// <summary>"primary" or a DXGI output number (see Rewind.exe --list).</summary>
        public readonly string Monitor;
        public readonly bool GameAudio;
        public readonly bool Mic;
        /// <summary>ffmpeg audio filter chain applied to the mic track; empty = none.</summary>
        public readonly string MicFilter;
        /// <summary>Shifts both audio tracks later (+) or earlier (-) against the video.</summary>
        public readonly int AudioOffsetMs;
        public readonly string ClipsFolder;
        /// <summary>Explicit ffmpeg.exe path; empty = find it on PATH.</summary>
        public readonly string FfmpegPath;

        private Config(string hotkey, int seconds, int fps, int bitrateMbps, string codec, string monitor,
            bool gameAudio, bool mic, string micFilter, int audioOffsetMs, string clipsFolder, string ffmpegPath)
        {
            Hotkey = hotkey;
            Seconds = seconds;
            Fps = fps;
            BitrateMbps = bitrateMbps;
            Codec = codec;
            Monitor = monitor;
            GameAudio = gameAudio;
            Mic = mic;
            MicFilter = micFilter;
            AudioOffsetMs = audioOffsetMs;
            ClipsFolder = clipsFolder;
            FfmpegPath = ffmpegPath;
        }

        public static Config Defaults()
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            return new Config("ctrl+alt+p", 60, 60, 20, "h264", "primary", true, true,
                "afftdn=nr=12:nf=-40", 0, Path.Combine(videos, "Rewind"), "");
        }

        /// <summary>Reads the file, writing the default one first if it doesn't exist yet.</summary>
        public static Config Load(string path)
        {
            if (!File.Exists(path)) File.WriteAllText(path, DefaultText(), Encoding.UTF8);
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        public static Config Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            var values = ReadPairs(text);
            var d = Defaults();

            var hotkey = Get(values, "hotkey", d.Hotkey);
            HotkeySpec.Parse(hotkey); // validates; throws ConfigException with a clear message

            var seconds = GetInt(values, "seconds", d.Seconds, MinSeconds, MaxSeconds);
            var fps = GetInt(values, "fps", d.Fps, MinFps, MaxFps);
            var bitrate = GetInt(values, "bitrate_mbps", d.BitrateMbps, MinBitrate, MaxBitrate);
            var audioOffset = GetInt(values, "audio_offset_ms", d.AudioOffsetMs, -MaxAudioOffsetMs, MaxAudioOffsetMs);

            var codec = Get(values, "codec", d.Codec).ToLowerInvariant();
            if (codec != "h264" && codec != "hevc" && codec != "av1")
                throw new ConfigException("codec must be h264, hevc or av1. Got: " + codec);

            var monitor = Get(values, "monitor", d.Monitor).ToLowerInvariant();
            int monitorIndex;
            if (monitor != "primary" && (!int.TryParse(monitor, out monitorIndex) || monitorIndex < 0))
                throw new ConfigException("monitor must be 'primary' or a number from Rewind.exe --list. Got: " + monitor);

            var gameAudio = GetBool(values, "game_audio", d.GameAudio);
            var mic = GetBool(values, "mic", d.Mic);
            var micFilter = Get(values, "mic_filter", d.MicFilter);
            if (micFilter.ToLowerInvariant() == "off") micFilter = "";

            var clips = Get(values, "clips", d.ClipsFolder);
            if (clips.Length == 0) throw new ConfigException("clips folder is empty.");
            if (clips.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new ConfigException("clips folder has characters a path can't have: " + clips);

            var ffmpeg = Get(values, "ffmpeg", d.FfmpegPath);

            return new Config(hotkey, seconds, fps, bitrate, codec, monitor,
                gameAudio, mic, micFilter, audioOffset, clips, ffmpeg);
        }

        private static Dictionary<string, string> ReadPairs(string text)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = StripComment(lines[i]).Trim();
                if (line.Length == 0) continue;

                var eq = line.IndexOf('=');
                if (eq <= 0)
                    throw new ConfigException(string.Format("Line {0} isn't 'setting=value': {1}", i + 1, line));

                var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                var value = line.Substring(eq + 1).Trim();
                if (Array.IndexOf(KnownKeys, key) < 0)
                    throw new ConfigException(string.Format("Line {0}: '{1}' isn't a setting Rewind knows.", i + 1, key));
                if (values.ContainsKey(key))
                    throw new ConfigException(string.Format("'{0}' is set twice (line {1}).", key, i + 1));
                values[key] = value;
            }
            return values;
        }

        /// <summary>A line starting with # is a comment; so is anything after " #" mid-line.</summary>
        private static string StripComment(string line)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("#")) return "";
            var inline = line.IndexOf(" #", StringComparison.Ordinal);
            return inline >= 0 ? line.Substring(0, inline) : line;
        }

        private static string Get(Dictionary<string, string> values, string key, string fallback)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : fallback;
        }

        private static int GetInt(Dictionary<string, string> values, string key, int fallback, int min, int max)
        {
            string raw;
            if (!values.TryGetValue(key, out raw)) return fallback;
            int value;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                throw new ConfigException(key + " must be a whole number. Got: " + raw);
            if (value < min || value > max)
                throw new ConfigException(string.Format("{0} must be between {1} and {2}. Got: {3}", key, min, max, value));
            return value;
        }

        private static bool GetBool(Dictionary<string, string> values, string key, bool fallback)
        {
            string raw;
            if (!values.TryGetValue(key, out raw)) return fallback;
            switch (raw.Trim().ToLowerInvariant())
            {
                case "on": case "true": case "yes": case "1": return true;
                case "off": case "false": case "no": case "0": return false;
            }
            throw new ConfigException(key + " must be on or off. Got: " + raw);
        }

        /// <summary>The commented config.txt written on first run.</summary>
        public static string DefaultText()
        {
            var d = Defaults();
            var sb = new StringBuilder();
            sb.AppendLine("# Rewind settings. Change a line, then right-click the tray icon -> Reload config.");
            sb.AppendLine("# Lines starting with # are comments.");
            sb.AppendLine();
            sb.AppendLine("# Press this to save the last <seconds> as a clip. Examples: ctrl+alt+p, F9, shift+F10");
            sb.AppendLine("hotkey=" + d.Hotkey);
            sb.AppendLine();
            sb.AppendLine("# How far back a clip reaches, in seconds (5-600). Memory use is about bitrate x seconds / 8 MB.");
            sb.AppendLine("seconds=" + d.Seconds);
            sb.AppendLine();
            sb.AppendLine("# Frames per second to record (15-240).");
            sb.AppendLine("fps=" + d.Fps);
            sb.AppendLine();
            sb.AppendLine("# Video quality in megabits per second (2-150). 20 is plenty for 1440p60 h264.");
            sb.AppendLine("bitrate_mbps=" + d.BitrateMbps);
            sb.AppendLine();
            sb.AppendLine("# h264 plays everywhere. av1 = smaller files, same quality, needs a newer phone to play.");
            sb.AppendLine("codec=" + d.Codec);
            sb.AppendLine();
            sb.AppendLine("# primary = the main monitor. Or a number: run  Rewind.exe --list  to see them.");
            sb.AppendLine("monitor=" + d.Monitor);
            sb.AppendLine();
            sb.AppendLine("# Record what comes out of the speakers/headset (track 1).");
            sb.AppendLine("game_audio=" + (d.GameAudio ? "on" : "off"));
            sb.AppendLine();
            sb.AppendLine("# Record the microphone as its own track (track 2).");
            sb.AppendLine("mic=" + (d.Mic ? "on" : "off"));
            sb.AppendLine();
            sb.AppendLine("# Cleanup applied to the mic (an ffmpeg audio filter). off = raw mic.");
            sb.AppendLine("mic_filter=" + d.MicFilter);
            sb.AppendLine();
            sb.AppendLine("# If sound lands late or early against the picture, shift it here (milliseconds, + = later).");
            sb.AppendLine("audio_offset_ms=" + d.AudioOffsetMs);
            sb.AppendLine();
            sb.AppendLine("# Where clips go.");
            sb.AppendLine("clips=" + d.ClipsFolder);
            sb.AppendLine();
            sb.AppendLine("# Leave empty to use the ffmpeg on PATH, or give a full path to ffmpeg.exe.");
            sb.AppendLine("ffmpeg=");
            return sb.ToString();
        }
    }
}
