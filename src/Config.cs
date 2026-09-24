using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    /// nothing edits it afterwards; "Reload config" in the tray builds another. Text() writes one
    /// back out with its comments, which is how the settings window saves.
    /// </summary>
    internal sealed class Config
    {
        public const int MinSeconds = 5, MaxSeconds = 600;
        public const int MinShortSeconds = 3;
        public const int MinFps = 15, MaxFps = 240;
        public const int MinBitrate = 2, MaxBitrate = 150;
        public const int MaxAudioOffsetMs = 2000;
        public const int MinGraceSeconds = 5, MaxGraceSeconds = 600;
        public const int MinRecordingMinutes = 1, MaxRecordingMinutes = 600;
        public const int MinShareMb = 1, MaxShareMb = 500;
        public const int MaxStorageGbLimit = 5000;
        public const string RecordAlways = "always", RecordGames = "games";

        private static readonly string[] KnownKeys =
        {
            "hotkey", "hotkey_short", "hotkey_record", "hotkey_screenshot", "seconds", "short_seconds", "fps", "bitrate_mbps", "codec", "monitor",
            "game_audio", "mic", "mic_filter", "audio_offset_ms", "record", "games", "game_grace_seconds",
            "recording_max_minutes", "share_max_mb", "max_storage_gb", "toast", "sound", "sound_volume", "voice_clip", "voice_phrase",
            "voice_engine", "voice_url", "clips", "ffmpeg", "auto_update"
        };
        public const string VoiceWindows = "windows", VoiceRioVoice = "riovoice";

        private static readonly string[] DefaultGames =
        {
            "javaw", "FortniteClient-Win64-Shipping", "RobloxPlayerBeta", "RocketLeague", "GeometryDash", "Minecraft.Windows"
        };

        public readonly string Hotkey;
        /// <summary>Second hotkey for a short clip; empty = none.</summary>
        public readonly string HotkeyShort;
        /// <summary>Starts and stops a long recording; empty = none.</summary>
        public readonly string HotkeyRecord;
        /// <summary>Saves the latest frame as a PNG; empty = none.</summary>
        public readonly string HotkeyScreenshot;
        public readonly int Seconds;
        public readonly int ShortSeconds;
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
        /// <summary>"always" or "games" (only record while a game is in front).</summary>
        public readonly string Record;
        /// <summary>Process names that count as a game even when windowed.</summary>
        public readonly ReadOnlyCollection<string> Games;
        /// <summary>In games mode: seconds a game may be out of front before recording pauses.</summary>
        public readonly int GameGraceSeconds;
        /// <summary>A long recording stops itself after this many minutes.</summary>
        public readonly int RecordingMaxMinutes;
        /// <summary>"Copy for Discord" shrinks a clip to fit under this many MB.</summary>
        public readonly int ShareMaxMb;
        /// <summary>Oldest clips are deleted once the folder passes this size; 0 = never.</summary>
        public readonly int MaxStorageGb;
        /// <summary>Show the on-screen "Clip saved" card (visible over most games) instead of a tray balloon.</summary>
        public readonly bool Toast;
        /// <summary>Play Rewind's own chime when a clip / screenshot / recording lands.</summary>
        public readonly bool Sound;
        /// <summary>0-100.</summary>
        public readonly int SoundVolume;
        /// <summary>Listen on the mic for the phrase and save a clip when it is heard.</summary>
        public readonly bool VoiceClip;
        public readonly string VoicePhrase;
        /// <summary>"windows" = the offline recogniser built into Windows; "riovoice" = Rio's own speech-to-text over a WebSocket.</summary>
        public readonly string VoiceEngine;
        /// <summary>The RioVoice streaming endpoint (ws:// or wss://).</summary>
        public readonly string VoiceUrl;
        public readonly string ClipsFolder;
        /// <summary>Explicit ffmpeg.exe path; empty = find it on PATH.</summary>
        public readonly string FfmpegPath;
        /// <summary>Check GitHub for a newer Rewind every few hours and swap it in when idle.</summary>
        public readonly bool AutoUpdate;

        private Config(string hotkey, string hotkeyShort, string hotkeyRecord, string hotkeyScreenshot, int seconds, int shortSeconds, int fps, int bitrateMbps,
            string codec, string monitor, bool gameAudio, bool mic, string micFilter, int audioOffsetMs,
            string record, IList<string> games, int gameGraceSeconds, int recordingMaxMinutes, int shareMaxMb, int maxStorageGb, bool toast,
            bool sound, int soundVolume, bool voiceClip, string voicePhrase, string voiceEngine, string voiceUrl, string clipsFolder, string ffmpegPath, bool autoUpdate)
        {
            Hotkey = hotkey;
            HotkeyShort = hotkeyShort;
            HotkeyRecord = hotkeyRecord;
            HotkeyScreenshot = hotkeyScreenshot;
            Seconds = seconds;
            ShortSeconds = shortSeconds;
            Fps = fps;
            BitrateMbps = bitrateMbps;
            Codec = codec;
            Monitor = monitor;
            GameAudio = gameAudio;
            Mic = mic;
            MicFilter = micFilter;
            AudioOffsetMs = audioOffsetMs;
            Record = record;
            Games = new ReadOnlyCollection<string>(new List<string>(games));
            GameGraceSeconds = gameGraceSeconds;
            RecordingMaxMinutes = recordingMaxMinutes;
            ShareMaxMb = shareMaxMb;
            MaxStorageGb = maxStorageGb;
            Toast = toast;
            Sound = sound;
            SoundVolume = soundVolume;
            VoiceClip = voiceClip;
            VoicePhrase = voicePhrase;
            VoiceEngine = voiceEngine;
            VoiceUrl = voiceUrl;
            ClipsFolder = clipsFolder;
            FfmpegPath = ffmpegPath;
            AutoUpdate = autoUpdate;
        }

        public bool GamesOnly { get { return Record == RecordGames; } }

        public static Config Defaults()
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            return new Config("ctrl+alt+p", "ctrl+alt+o", "ctrl+alt+r", "ctrl+alt+i", 60, 15, 60, 20, "h264", "primary", true, true,
                "afftdn=nr=12:nf=-40", 0, RecordAlways, DefaultGames, 45, 120, 20, 0, true, true, 80, true, "clip that",
                VoiceWindows, "wss://thoughts.riomax.com/ws/transcribe", Path.Combine(videos, "Rewind"), "", true);
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
            var hotkeyShort = OptionalHotkey(values, "hotkey_short", d.HotkeyShort);
            var hotkeyRecord = OptionalHotkey(values, "hotkey_record", d.HotkeyRecord);
            var hotkeyScreenshot = OptionalHotkey(values, "hotkey_screenshot", d.HotkeyScreenshot);
            CheckDistinct(new[] { "hotkey", "hotkey_short", "hotkey_record", "hotkey_screenshot" },
                new[] { hotkey, hotkeyShort, hotkeyRecord, hotkeyScreenshot });

            var seconds = GetInt(values, "seconds", d.Seconds, MinSeconds, MaxSeconds);
            var shortSeconds = GetInt(values, "short_seconds", d.ShortSeconds, MinShortSeconds, MaxSeconds);
            if (shortSeconds >= seconds)
                throw new ConfigException(string.Format("short_seconds ({0}) must be less than seconds ({1}).", shortSeconds, seconds));
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

            var record = Get(values, "record", d.Record).ToLowerInvariant();
            if (record != RecordAlways && record != RecordGames)
                throw new ConfigException("record must be always or games. Got: " + record);
            var games = ParseGames(Get(values, "games", string.Join(", ", d.Games)));
            var grace = GetInt(values, "game_grace_seconds", d.GameGraceSeconds, MinGraceSeconds, MaxGraceSeconds);

            var recordingMax = GetInt(values, "recording_max_minutes", d.RecordingMaxMinutes, MinRecordingMinutes, MaxRecordingMinutes);
            var shareMax = GetInt(values, "share_max_mb", d.ShareMaxMb, MinShareMb, MaxShareMb);
            var storageGb = GetInt(values, "max_storage_gb", d.MaxStorageGb, 0, MaxStorageGbLimit);
            var toast = GetBool(values, "toast", d.Toast);
            var sound = GetBool(values, "sound", d.Sound);
            var soundVolume = GetInt(values, "sound_volume", d.SoundVolume, 0, 100);
            var voiceClip = GetBool(values, "voice_clip", d.VoiceClip);
            var voicePhrase = Get(values, "voice_phrase", d.VoicePhrase).Trim();
            if (voicePhrase.Length == 0) voicePhrase = d.VoicePhrase;
            if (voicePhrase.Length > 40) throw new ConfigException("voice_phrase is too long; keep it to a few words. Got: " + voicePhrase);
            var voiceEngine = Get(values, "voice_engine", d.VoiceEngine).Trim().ToLowerInvariant();
            if (voiceEngine != VoiceWindows && voiceEngine != VoiceRioVoice)
                throw new ConfigException("voice_engine must be windows or riovoice. Got: " + voiceEngine);
            var voiceUrl = Get(values, "voice_url", d.VoiceUrl).Trim();
            if (voiceUrl.Length == 0) voiceUrl = d.VoiceUrl;
            Uri parsedUrl;
            if (!Uri.TryCreate(voiceUrl, UriKind.Absolute, out parsedUrl) || (parsedUrl.Scheme != "ws" && parsedUrl.Scheme != "wss"))
                throw new ConfigException("voice_url must start with ws:// or wss://. Got: " + voiceUrl);

            var clips = Get(values, "clips", d.ClipsFolder);
            if (clips.Length == 0) clips = d.ClipsFolder; // empty = this user's Videos\Rewind, so config.example.txt carries no one's path
            if (clips.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new ConfigException("clips folder has characters a path can't have: " + clips);

            var ffmpeg = Get(values, "ffmpeg", d.FfmpegPath);
            var autoUpdate = GetBool(values, "auto_update", d.AutoUpdate);

            return new Config(hotkey, hotkeyShort, hotkeyRecord, hotkeyScreenshot, seconds, shortSeconds, fps, bitrate, codec, monitor,
                gameAudio, mic, micFilter, audioOffset, record, games, grace, recordingMax, shareMax, storageGb, toast,
                sound, soundVolume, voiceClip, voicePhrase, voiceEngine, voiceUrl, clips, ffmpeg, autoUpdate);
        }

        /// <summary>A hotkey that may be "off" (returned as empty); anything else must parse.</summary>
        private static string OptionalHotkey(Dictionary<string, string> values, string key, string fallback)
        {
            var text = Get(values, key, fallback);
            if (text.Trim().ToLowerInvariant() == "off" || text.Trim().Length == 0) return "";
            HotkeySpec.Parse(text);
            return text;
        }

        /// <summary>Two settings on the same key would fight over it: refuse with both names.</summary>
        private static void CheckDistinct(string[] names, string[] hotkeys)
        {
            for (var i = 0; i < hotkeys.Length; i++)
            {
                if (hotkeys[i].Length == 0) continue;
                var a = HotkeySpec.Parse(hotkeys[i]);
                for (var j = i + 1; j < hotkeys.Length; j++)
                {
                    if (hotkeys[j].Length == 0) continue;
                    var b = HotkeySpec.Parse(hotkeys[j]);
                    if (a.Modifiers == b.Modifiers && a.VirtualKey == b.VirtualKey)
                        throw new ConfigException(string.Format("{0} and {1} are the same key ({2}). Give each its own key, or set one of them to off.", names[i], names[j], a.Text));
                }
            }
        }

        /// <summary>"javaw, cs2.exe; Roblox" -> javaw, cs2, Roblox: trimmed, .exe dropped, empties and repeats gone.</summary>
        public static ReadOnlyCollection<string> ParseGames(string raw)
        {
            var games = new List<string>();
            foreach (var part in (raw ?? "").Split(',', ';'))
            {
                var name = part.Trim();
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4).Trim();
                if (name.Length == 0) continue;
                var seen = false;
                foreach (var existing in games) if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)) seen = true;
                if (!seen) games.Add(name);
            }
            return new ReadOnlyCollection<string>(games);
        }

        /// <summary>The settings as config.txt strings, keyed by setting name. A fresh dictionary each call.</summary>
        public IDictionary<string, string> Values()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            values["hotkey"] = Hotkey;
            values["hotkey_short"] = HotkeyShort.Length > 0 ? HotkeyShort : "off";
            values["hotkey_record"] = HotkeyRecord.Length > 0 ? HotkeyRecord : "off";
            values["hotkey_screenshot"] = HotkeyScreenshot.Length > 0 ? HotkeyScreenshot : "off";
            values["seconds"] = Seconds.ToString(CultureInfo.InvariantCulture);
            values["short_seconds"] = ShortSeconds.ToString(CultureInfo.InvariantCulture);
            values["fps"] = Fps.ToString(CultureInfo.InvariantCulture);
            values["bitrate_mbps"] = BitrateMbps.ToString(CultureInfo.InvariantCulture);
            values["codec"] = Codec;
            values["monitor"] = Monitor;
            values["game_audio"] = GameAudio ? "on" : "off";
            values["mic"] = Mic ? "on" : "off";
            values["mic_filter"] = MicFilter.Length > 0 ? MicFilter : "off";
            values["audio_offset_ms"] = AudioOffsetMs.ToString(CultureInfo.InvariantCulture);
            values["record"] = Record;
            values["games"] = string.Join(", ", Games);
            values["game_grace_seconds"] = GameGraceSeconds.ToString(CultureInfo.InvariantCulture);
            values["recording_max_minutes"] = RecordingMaxMinutes.ToString(CultureInfo.InvariantCulture);
            values["share_max_mb"] = ShareMaxMb.ToString(CultureInfo.InvariantCulture);
            values["max_storage_gb"] = MaxStorageGb.ToString(CultureInfo.InvariantCulture);
            values["toast"] = Toast ? "on" : "off";
            values["sound"] = Sound ? "on" : "off";
            values["sound_volume"] = SoundVolume.ToString(CultureInfo.InvariantCulture);
            values["voice_clip"] = VoiceClip ? "on" : "off";
            values["voice_phrase"] = VoicePhrase;
            values["voice_engine"] = VoiceEngine;
            values["voice_url"] = VoiceUrl;
            values["clips"] = ClipsFolder;
            values["ffmpeg"] = FfmpegPath;
            values["auto_update"] = AutoUpdate ? "on" : "off";
            return values;
        }

        public string Text()
        {
            return Text(Values());
        }

        /// <summary>The commented config.txt written on first run.</summary>
        public static string DefaultText()
        {
            return Defaults().Text();
        }

        /// <summary>Renders a full config.txt with comments; settings missing from values get their defaults.</summary>
        public static string Text(IDictionary<string, string> values)
        {
            if (values == null) throw new ArgumentNullException("values");
            var d = Defaults().Values();
            var sb = new StringBuilder();
            sb.AppendLine("# Rewind settings. Change a line, then right-click the tray icon -> Reload config.");
            sb.AppendLine("# Lines starting with # are comments.");
            sb.AppendLine();
            Line(sb, values, d, "hotkey", "Press this to save the last <seconds> as a clip. Examples: ctrl+alt+p, F9, shift+F10");
            Line(sb, values, d, "hotkey_short", "A second key that saves just the last <short_seconds>. off = no second key.");
            Line(sb, values, d, "hotkey_record", "Starts a long recording; press again to stop and save it. off = no key.");
            Line(sb, values, d, "hotkey_screenshot", "Saves the latest frame as a PNG (and copies it, ready to paste). off = no key.");
            Line(sb, values, d, "seconds", "How far back a clip reaches, in seconds (5-600). Memory use is about bitrate x seconds / 8 MB.");
            Line(sb, values, d, "short_seconds", "How far back the short clip reaches, in seconds (3 up to seconds-1).");
            Line(sb, values, d, "fps", "Frames per second to record (15-240).");
            Line(sb, values, d, "bitrate_mbps", "Video quality in megabits per second (2-150). 20 is plenty for 1440p60 h264.");
            Line(sb, values, d, "codec", "h264 plays everywhere. av1 = smaller files, same quality, needs a newer phone to play.");
            Line(sb, values, d, "monitor", "primary = the main monitor. Or a number: run  Rewind.exe --list  to see them.");
            Line(sb, values, d, "game_audio", "Record what comes out of the speakers/headset (track 1).");
            Line(sb, values, d, "mic", "Record the microphone as its own track (track 2).");
            Line(sb, values, d, "mic_filter", "Cleanup applied to the mic (an ffmpeg audio filter). off = raw mic.");
            Line(sb, values, d, "audio_offset_ms", "If sound lands late or early against the picture, shift it here (milliseconds, + = later).");
            Line(sb, values, d, "record", "always = record all the time. games = only while a game is in front (see games= below).");
            Line(sb, values, d, "games", "Apps that count as a game even in a window (process names, comma separated). Any app covering the whole monitor counts too.");
            Line(sb, values, d, "game_grace_seconds", "In games mode: how long a game can be out of front before recording pauses (5-600 s).");
            Line(sb, values, d, "recording_max_minutes", "A long recording stops itself after this many minutes (1-600). About 150 MB per minute at 20 Mbps.");
            Line(sb, values, d, "share_max_mb", "Copy for Discord shrinks a clip to fit under this many MB (1-500). Discord's free limit is 20.");
            Line(sb, values, d, "max_storage_gb", "Delete the oldest clips once the clips folder passes this many GB. 0 = never delete anything.");
            Line(sb, values, d, "toast", "on = a small \"Clip saved\" card on screen (shows over most games). off = tray balloons only.");
            Line(sb, values, d, "sound", "Play a chime when a clip, screenshot or recording lands. Drop your own clip.wav next to Rewind.exe to change it.");
            Line(sb, values, d, "sound_volume", "How loud the chime is (0-100).");
            Line(sb, values, d, "voice_clip", "on = saying the phrase below into the mic saves a clip, like Medal's \"clip that\".");
            Line(sb, values, d, "voice_phrase", "What to say. Two or three clear words work best.");
            Line(sb, values, d, "voice_engine", "windows = the recogniser built into Windows (offline). riovoice = a RioVoice speech-to-text server (more accurate).");
            Line(sb, values, d, "voice_url", "The RioVoice streaming address (only used with voice_engine=riovoice).");
            Line(sb, values, d, "clips", "Where clips go. Leave empty for your own Videos\\Rewind folder.");
            Line(sb, values, d, "ffmpeg", "Leave empty to use the ffmpeg on PATH, or give a full path to ffmpeg.exe.");
            Line(sb, values, d, "auto_update", "on = every 6 hours, fetch a newer Rewind from GitHub, check its SHA-256 and restart into it once nothing is recording. off = never.");
            return sb.ToString().TrimEnd() + Environment.NewLine;
        }

        /// <summary>The default clips folder is written as an empty value: the file then works on any PC.</summary>
        private static string Shown(string key, string value, IDictionary<string, string> defaults)
        {
            return key == "clips" && string.Equals(value, defaults[key], StringComparison.OrdinalIgnoreCase) ? "" : value;
        }

        private static void Line(StringBuilder sb, IDictionary<string, string> values, IDictionary<string, string> defaults, string key, string comment)
        {
            string value;
            if (!values.TryGetValue(key, out value) || value == null) value = defaults[key];
            sb.AppendLine("# " + comment);
            sb.AppendLine(key + "=" + Shown(key, value.Trim(), defaults));
            sb.AppendLine();
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
    }
}
