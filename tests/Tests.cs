using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Rewind;

namespace Rewind.Tests
{
    /// <summary>
    /// Plain asserts, no framework: the in-box C# compiler is all this project needs. Covers the
    /// parts that can be wrong without a GPU or a microphone: config parsing, hotkey parsing, the
    /// ring buffer, the keyframe cut, the ffmpeg command lines and the file-name cleanup.
    /// </summary>
    internal static partial class Tests
    {
        private static int _passed, _failed;

        private static int Main()
        {
            Run("config: empty text gives the defaults", () =>
            {
                var c = Config.Parse("");
                Equal(60, c.Seconds); Equal(60, c.Fps); Equal(20, c.BitrateMbps);
                Equal("h264", c.Codec); Equal("primary", c.Monitor); Equal("ctrl+alt+p", c.Hotkey);
                True(c.GameAudio, "game audio on"); True(c.Mic, "mic on"); Equal(0, c.AudioOffsetMs);
                True(c.ClipsFolder.EndsWith("Rewind"), "clips folder ends with Rewind");
            });
            Run("config: the default file parses back to the defaults", () =>
            {
                var c = Config.Parse(Config.DefaultText());
                var d = Config.Defaults();
                Equal(d.Seconds, c.Seconds); Equal(d.MicFilter, c.MicFilter); Equal(d.ClipsFolder, c.ClipsFolder); Equal("", c.FfmpegPath);
            });
            Run("config: config.example.txt is the default config, byte for byte", () =>
            {
                var example = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.example.txt"));
                Equal(Config.DefaultText().Replace("\r\n", "\n").Trim(), example.Replace("\r\n", "\n").Trim());
            });
            Run("config: values, inline comments and case", () =>
            {
                var c = Config.Parse("Seconds=30 # half a minute\nFPS=120\ncodec=AV1\nmonitor=2\nmic=off\nmic_filter=OFF\naudio_offset_ms=-120\nbitrate_mbps=35");
                Equal(30, c.Seconds); Equal(120, c.Fps); Equal("av1", c.Codec); Equal("2", c.Monitor);
                True(!c.Mic, "mic off"); Equal("", c.MicFilter); Equal(-120, c.AudioOffsetMs); Equal(35, c.BitrateMbps);
            });
            Run("config: unknown key is refused", () => Throws<ConfigException>(() => Config.Parse("bogus=1")));
            Run("config: seconds out of range is refused", () => Throws<ConfigException>(() => Config.Parse("seconds=1000")));
            Run("config: non-number is refused", () => Throws<ConfigException>(() => Config.Parse("fps=fast")));
            Run("config: bad bool is refused", () => Throws<ConfigException>(() => Config.Parse("mic=maybe")));
            Run("config: bad codec is refused", () => Throws<ConfigException>(() => Config.Parse("codec=vp9")));
            Run("config: bad monitor is refused", () => Throws<ConfigException>(() => Config.Parse("monitor=left")));
            Run("config: duplicate key is refused", () => Throws<ConfigException>(() => Config.Parse("seconds=10\nseconds=20")));
            Run("config: line without = is refused", () => Throws<ConfigException>(() => Config.Parse("seconds")));
            Run("config: bad hotkey is refused", () => Throws<ConfigException>(() => Config.Parse("hotkey=ctrl+banana")));
            Run("config: new defaults (short hotkey, games mode)", () =>
            {
                var c = Config.Parse("");
                Equal("ctrl+alt+o", c.HotkeyShort); Equal(15, c.ShortSeconds); Equal("always", c.Record); True(!c.GamesOnly, "always");
                Equal(45, c.GameGraceSeconds); Equal(6, c.Games.Count); Equal("javaw", c.Games[0]);
            });
            Run("config: hotkey_short=off disables it", () => Equal("", Config.Parse("hotkey_short=OFF").HotkeyShort));
            Run("config: same key twice is refused", () => Throws<ConfigException>(() => Config.Parse("hotkey=ctrl+alt+p\nhotkey_short=Alt+Ctrl+P")));
            Run("config: short_seconds must be below seconds", () =>
            {
                Throws<ConfigException>(() => Config.Parse("seconds=20\nshort_seconds=20"));
                Equal(19, Config.Parse("seconds=20\nshort_seconds=19").ShortSeconds);
            });
            Run("config: bad record mode is refused", () => Throws<ConfigException>(() => Config.Parse("record=sometimes")));
            Run("config: grace out of range is refused", () => Throws<ConfigException>(() => Config.Parse("game_grace_seconds=2")));
            Run("config: games list is trimmed, .exe dropped, repeats gone", () =>
            {
                var games = Config.ParseGames(" javaw , cs2.exe;Roblox,,JAVAW, RocketLeague.EXE ");
                Equal("javaw|cs2|Roblox|RocketLeague", string.Join("|", games));
                Equal(0, Config.ParseGames("").Count);
            });
            Run("config: text round-trips a non-default config", () =>
            {
                var c = Config.Parse("hotkey=F9\nhotkey_short=shift+F9\nseconds=90\nshort_seconds=20\nfps=30\nbitrate_mbps=8\ncodec=hevc\nmonitor=1\n"
                    + "game_audio=off\nmic=off\nmic_filter=off\naudio_offset_ms=50\nclips=D:\\Clips\nffmpeg=C:\\ff\\ffmpeg.exe\nrecord=games\ngames=javaw, cs2.exe\ngame_grace_seconds=30");
                var text = c.Text();
                Contains(text, "# Where clips go. Leave empty for your own Videos\\Rewind folder." + Environment.NewLine + "clips=D:\\Clips" + Environment.NewLine);
                var back = Config.Parse(text);
                Equal("F9", back.Hotkey); Equal("shift+F9", back.HotkeyShort); Equal(90, back.Seconds); Equal(20, back.ShortSeconds);
                Equal(30, back.Fps); Equal(8, back.BitrateMbps); Equal("hevc", back.Codec); Equal("1", back.Monitor);
                True(!back.GameAudio && !back.Mic, "audio off"); Equal("", back.MicFilter); Equal(50, back.AudioOffsetMs);
                Equal(@"D:\Clips", back.ClipsFolder); Equal(@"C:\ff\ffmpeg.exe", back.FfmpegPath); True(back.GamesOnly, "games mode");
                Equal("javaw|cs2", string.Join("|", back.Games)); Equal(30, back.GameGraceSeconds);
            });
            Run("config: text with missing values falls back to defaults", () =>
            {
                var text = Config.Text(new Dictionary<string, string> { { "seconds", "30" } });
                var c = Config.Parse(text);
                Equal(30, c.Seconds); Equal(60, c.Fps); Equal("ctrl+alt+p", c.Hotkey);
            });

            Run("hotkey: ctrl+alt+p", () =>
            {
                var h = HotkeySpec.Parse("ctrl+alt+p");
                Equal(HotkeySpec.ModControl | HotkeySpec.ModAlt, h.Modifiers); Equal(0x50u, h.VirtualKey); Equal("Ctrl+Alt+P", h.Text);
            });
            Run("hotkey: bare F9", () =>
            {
                var h = HotkeySpec.Parse("F9");
                Equal(0u, h.Modifiers); Equal(0x78u, h.VirtualKey); Equal("F9", h.Text);
            });
            Run("hotkey: shift+5 is the digit key", () =>
            {
                var h = HotkeySpec.Parse(" Shift + 5 ");
                Equal(HotkeySpec.ModShift, h.Modifiers); Equal(0x35u, h.VirtualKey); Equal("Shift+5", h.Text);
            });
            Run("hotkey: modifiers only is refused", () => Throws<ConfigException>(() => HotkeySpec.Parse("ctrl+alt")));
            Run("hotkey: two keys is refused", () => Throws<ConfigException>(() => HotkeySpec.Parse("a+b")));
            Run("hotkey: modifier as the key is refused", () => Throws<ConfigException>(() => HotkeySpec.Parse("ctrl+shiftkey")));
            Run("hotkey: empty is refused", () => Throws<ConfigException>(() => HotkeySpec.Parse("  ")));
            Run("hotkey box: a pressed combo becomes config text", () =>
            {
                Equal("ctrl+alt+p", HotkeySpec.FromKeys(Keys.Control | Keys.Alt | Keys.P));
                Equal("F9", HotkeySpec.FromKeys(Keys.F9));
                Equal("shift+F10", HotkeySpec.FromKeys(Keys.Shift | Keys.F10));
                Equal("ctrl+5", HotkeySpec.FromKeys(Keys.Control | Keys.D5));
            });
            Run("hotkey box: modifiers alone are not a hotkey yet", () =>
            {
                Equal<string>(null, HotkeySpec.FromKeys(Keys.Control | Keys.Alt | Keys.Menu));
                Equal("ctrl+alt+", HotkeySpec.Held(Keys.Control | Keys.Alt));
            });
            Run("hotkey box: every pressed combo parses back to the same keys", () =>
            {
                foreach (var keys in new[] { Keys.Control | Keys.Alt | Keys.P, Keys.F9, Keys.Shift | Keys.F10, Keys.Alt | Keys.D5, Keys.Control | Keys.OemMinus, Keys.Control | Keys.Shift | Keys.NumPad7 })
                {
                    var h = HotkeySpec.Parse(HotkeySpec.FromKeys(keys));
                    Equal((uint)(keys & Keys.KeyCode), h.VirtualKey);
                }
            });

            Run("ring: keeps only the window", () =>
            {
                var ring = new ChunkRing(TimeSpan.FromSeconds(10));
                var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                ring.Add(new byte[100], 100, t0);
                ring.Add(new byte[50], 50, t0.AddSeconds(5));
                ring.Add(new byte[10], 10, t0.AddSeconds(11));
                Equal(60L, ring.Bytes); Equal(TimeSpan.FromSeconds(6), ring.Span);
            });
            Run("ring: snapshot is the last span, in order, and a copy", () =>
            {
                var ring = new ChunkRing(TimeSpan.FromSeconds(60));
                var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var first = new byte[] { 1, 2, 3 };
                ring.Add(first, 3, t0);
                ring.Add(new byte[] { 4, 5 }, 2, t0.AddSeconds(10));
                ring.Add(new byte[] { 6 }, 1, t0.AddSeconds(20));
                first[0] = 99; // the ring must have copied, not kept our array
                var all = ring.Snapshot(TimeSpan.FromSeconds(30), t0.AddSeconds(20));
                Equal("1,2,3,4,5,6", string.Join(",", all));
                var recent = ring.Snapshot(TimeSpan.FromSeconds(10), t0.AddSeconds(20));
                Equal("4,5,6", string.Join(",", recent));
                Equal(0, ring.Snapshot(TimeSpan.FromSeconds(1), t0.AddSeconds(60)).Length);
            });
            Run("ring: chunks are the ring's own arrays, oldest first", () =>
            {
                var ring = new ChunkRing(TimeSpan.FromSeconds(60));
                var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                ring.Add(new byte[] { 1 }, 1, t0);
                ring.Add(new byte[] { 2 }, 1, t0.AddSeconds(10));
                var chunks = ring.Chunks(TimeSpan.FromSeconds(30), t0.AddSeconds(10));
                Equal(2, chunks.Count); Equal((byte)1, chunks[0].Data[0]); Equal((byte)2, chunks[1].Data[0]);
                Equal(1, ring.Chunks(TimeSpan.FromSeconds(5), t0.AddSeconds(10)).Count);
            });
            Run("ring: partial add copies only count bytes", () =>
            {
                var ring = new ChunkRing(TimeSpan.FromSeconds(1));
                ring.Add(new byte[] { 7, 8, 9 }, 2, DateTime.UtcNow);
                Equal(2L, ring.Bytes);
            });
            Run("ring: clear empties it", () =>
            {
                var ring = new ChunkRing(TimeSpan.FromSeconds(1));
                ring.Add(new byte[] { 1 }, 1, DateTime.UtcNow);
                ring.Clear();
                Equal(0L, ring.Bytes); Equal(TimeSpan.Zero, ring.Span);
            });
            Run("ring: bad count is refused", () => Throws<ArgumentOutOfRangeException>(() => new ChunkRing(TimeSpan.FromSeconds(1)).Add(new byte[2], 3, DateTime.UtcNow)));
            Run("ring: zero window is refused", () => Throws<ArgumentOutOfRangeException>(() => new ChunkRing(TimeSpan.Zero)));

            var pat = TsPacket(0, true, false, Pat(4096));
            var pmt = TsPacket(4096, true, false, Pmt(new byte[] { 0x1b, 0x0f }, new[] { 256, 257 }));
            Run("tscut: cuts at the first video keyframe after PAT+PMT, across chunk edges", () =>
            {
                var junk = new byte[] { 1, 2, 3, 4, 5, 6, 7 };            // the torn tail of an evicted packet
                var frame = TsPacket(256, true, false, Payload(0xAA));    // a P-frame: not a cut
                var audioKey = TsPacket(257, true, true, Payload(0xBB));  // audio marks every packet: not a cut
                var key = TsPacket(256, true, true, Payload(0xCC));       // the keyframe
                var rest = TsPacket(256, false, false, Payload(0xDD));
                var all = Join(junk, pat, pmt, frame, audioKey, key, rest, rest, rest);
                var plan = TsCut.Plan(Split(all, 100, 400));
                True(plan.Clean, "clean cut");
                Equal(7L + 188 * 4, plan.TrimmedBytes);
                Equal(2, plan.StartChunk); Equal(259, plan.StartOffset);
                Equal(BitConverter.ToString(Join(pat, pmt)), BitConverter.ToString(plan.Prefix));
            });
            Run("tscut: a keyframe before the tables waits for the next one", () =>
            {
                var key = TsPacket(256, true, true, Payload(1));
                var plan = TsCut.Plan(Split(Join(pat, key, pmt, key, key), 1000));
                True(plan.Clean, "clean cut"); Equal(188L * 3, plan.TrimmedBytes);
            });
            Run("tscut: no keyframe means a raw cut from byte 0", () =>
            {
                var frame = TsPacket(256, true, false, Payload(9));
                var plan = TsCut.Plan(Split(Join(pat, pmt, frame, frame, frame), 300));
                True(!plan.Clean, "raw cut"); Equal(0, plan.StartChunk); Equal(0, plan.StartOffset);
                Equal(0, plan.Prefix.Length); Equal(0L, plan.TrimmedBytes);
            });
            Run("tscut: too little data is a raw cut, not a crash", () =>
            {
                True(!TsCut.Plan(new List<Chunk>()).Clean, "empty");
                True(!TsCut.Plan(Split(new byte[100], 50)).Clean, "tiny");
            });
            Run("tscut: video pid comes from the stream type; private data (av1) beats audio", () =>
            {
                var pmtAudioFirst = TsPacket(4096, true, false, Pmt(new byte[] { 0x0f, 0x24 }, new[] { 300, 301 }));
                var audio = TsPacket(300, true, true, Payload(1));
                var video = TsPacket(301, true, true, Payload(2));
                Equal(188L * 3, TsCut.Plan(Split(Join(pat, pmtAudioFirst, audio, video, video), 1000)).TrimmedBytes);
                var pmtAv1 = TsPacket(4096, true, false, Pmt(new byte[] { 0x0f, 0x06 }, new[] { 300, 302 }));
                var av1 = TsPacket(302, true, true, Payload(3));
                Equal(188L * 3, TsCut.Plan(Split(Join(pat, pmtAv1, audio, av1, av1), 1000)).TrimmedBytes);
            });

            Run("ffmpeg: capture line has every piece", () =>
            {
                var c = Config.Parse("seconds=30\nfps=60\nbitrate_mbps=20");
                var audio = new[]
                {
                    new AudioSource("Game", "rewind_game", "f32le", 48000, 2, ""),
                    new AudioSource("Mic", "rewind_mic", "s16le", 44100, 1, "afftdn=nr=12:nf=-40")
                };
                var a = FfmpegArgs.Capture(c, 2, audio);
                Contains(a, "-f lavfi -i \"ddagrab=output_idx=2:framerate=60:draw_mouse=1:output_fmt=8bit\"");
                Contains(a, "-f f32le -ar 48000 -ac 2 -ch_layout stereo -i \"\\\\.\\pipe\\rewind_game\"");
                Contains(a, "-f s16le -ar 44100 -ac 1 -ch_layout mono -i \"\\\\.\\pipe\\rewind_mic\"");
                Contains(a, "-map 0:v -map 1:a -map 2:a");
                Contains(a, "-c:v h264_nvenc -preset p5 -tune hq -profile:v high -rc cbr -b:v 20M -maxrate 20M -bufsize 40M -g 60 -forced-idr 1");
                Contains(a, "-filter:a:1 \"afftdn=nr=12:nf=-40\"");
                True(!a.Contains("-filter:a:0"), "no filter on the game track");
                Contains(a, "-c:a aac -b:a 192k -ar 48000");
                Contains(a, "-f mpegts -muxdelay 0 -muxpreload 0 -flush_packets 1 pipe:1");
                True(!a.Contains("-itsoffset"), "no offset by default");
            });
            Run("ffmpeg: audio offset goes on every audio input", () =>
            {
                var c = Config.Parse("audio_offset_ms=-120");
                var a = FfmpegArgs.Capture(c, 0, new[] { new AudioSource("Game", "g", "f32le", 48000, 2, ""), new AudioSource("Mic", "m", "f32le", 48000, 2, "") });
                Equal(2, Count(a, "-itsoffset -0.12 "));
            });
            Run("ffmpeg: no audio means video only", () =>
            {
                var a = FfmpegArgs.Capture(Config.Parse(""), 0, new AudioSource[0]);
                True(!a.Contains("-map 1:a") && !a.Contains("-c:a"), "no audio mapping");
            });
            Run("ffmpeg: av1 and hevc encoders", () =>
            {
                Contains(FfmpegArgs.Capture(Config.Parse("codec=av1"), 0, new AudioSource[0]), "-c:v av1_nvenc -preset p5 -tune hq -rc cbr");
                Contains(FfmpegArgs.Capture(Config.Parse("codec=hevc"), 0, new AudioSource[0]), "-c:v hevc_nvenc -preset p5 -tune hq -profile:v main -rc cbr");
            });
            Run("ffmpeg: remux line reads the ring from stdin", () =>
            {
                var a = FfmpegArgs.Remux(@"C:\x\Rewind clip.mp4", new[] { "Game", "Mic" }, "h264");
                Contains(a, "-loglevel error -nostdin -y -f mpegts -i pipe:0 -map 0:v -map 0:a? -c copy -metadata:s:a:0 handler_name=\"Game\" -metadata:s:a:1 handler_name=\"Mic\" -movflags +faststart \"C:\\x\\Rewind clip.mp4\"");
                True(!a.Contains("hvc1"), "no hvc1 tag for h264");
                Contains(FfmpegArgs.Remux("b.mp4", new string[0], "hevc"), "-c copy -tag:v hvc1 ");
            });
            Run("ffmpeg: quoting escapes inner quotes", () => Equal("\"a\\\"b\"", FfmpegArgs.Quote("a\"b")));
            Run("ffmpeg: audio source validates its format", () =>
            {
                Throws<ArgumentException>(() => new AudioSource("x", "p", "u8", 48000, 2, ""));
                Throws<ArgumentOutOfRangeException>(() => new AudioSource("x", "p", "f32le", 100, 2, ""));
            });

            Run("config: empty clips folder means this user's Videos\\Rewind, and is written back empty", () =>
            {
                Equal(Config.Defaults().ClipsFolder, Config.Parse("clips=").ClipsFolder);
                Equal(@"D:\clips", Config.Parse(@"clips=D:\clips").ClipsFolder);
                Contains(Config.Text(Config.Defaults().Values()), "clips=" + Environment.NewLine);
                Contains(Config.Text(Config.Parse(@"clips=D:\clips").Values()), @"clips=D:\clips");
            });

            Run("ffmpeg fetch: knows the zip entry and the progress text", () =>
            {
                True(FfmpegFetcher.IsFfmpegEntry("ffmpeg-master-latest-win64-gpl/bin/ffmpeg.exe"), "bin/ffmpeg.exe");
                True(!FfmpegFetcher.IsFfmpegEntry("ffmpeg-master-latest-win64-gpl/bin/ffprobe.exe"), "ffprobe is not it");
                True(!FfmpegFetcher.IsFfmpegEntry("ffmpeg-master-latest-win64-gpl/doc/ffmpeg.exe.txt"), "a doc is not it");
                Equal("43 MB of 110 MB", FfmpegFetcher.Progress(43 * 1048576L, 110 * 1048576L));
                Equal("43 MB", FfmpegFetcher.Progress(43 * 1048576L, -1));
            });
            Run("ffmpeg locator: next to the exe, then the fetched copy, then PATH", () =>
            {
                var c = FfmpegLocator.Candidates(null, @"C:\app", @"C:\data\ffmpeg", "C:\\one;\"C:\\two\";;");
                Equal(4, c.Count);
                Equal(@"C:\app\ffmpeg.exe", c[0]); Equal(@"C:\data\ffmpeg\ffmpeg.exe", c[1]);
                Equal(@"C:\one\ffmpeg.exe", c[2]); Equal(@"C:\two\ffmpeg.exe", c[3]);
                Equal(@"D:\x\ffmpeg.exe", FfmpegLocator.Candidates(@"D:\x\ffmpeg.exe", null, null, null)[0]);
            });

            var monitor = new Rectangle(0, 0, 2560, 1440);
            var none = new string[0];
            Run("games: a listed process counts even in a small window", () =>
                True(GameDetector.IsGame("JAVAW", new Rectangle(100, 100, 800, 600), monitor, true, new[] { "javaw" }), "listed, any case"));
            Run("games: a fullscreen app with no title bar counts", () =>
                True(GameDetector.IsGame("cs2", new Rectangle(0, 0, 2560, 1440), monitor, false, none), "fullscreen"));
            Run("games: a maximized window with a title bar does not", () =>
                True(!GameDetector.IsGame("chrome", new Rectangle(-8, -8, 2576, 1456), monitor, true, none), "maximized chrome"));
            Run("games: the desktop and shell never count", () =>
                True(!GameDetector.IsGame("explorer", new Rectangle(0, 0, 2560, 1440), monitor, false, none), "explorer"));
            Run("games: a small window does not", () =>
                True(!GameDetector.IsGame("cs2", new Rectangle(0, 0, 1280, 720), monitor, false, none), "small"));
            Run("games: nothing in front is not a game", () => True(!GameDetector.IsGame(ForegroundInfo.None, new[] { "javaw" }), "none"));
            Run("games: one pixel of slack on each edge", () =>
            {
                True(GameDetector.CoversMonitor(new Rectangle(1, 1, 2558, 1438), monitor), "1 px in");
                True(!GameDetector.CoversMonitor(new Rectangle(2, 0, 2558, 1440), monitor), "2 px in");
                True(!GameDetector.CoversMonitor(Rectangle.Empty, monitor), "empty");
            });
            Run("games: describe says why", () =>
            {
                Contains(GameDetector.Describe(new ForegroundInfo("chrome", new Rectangle(-8, -8, 2576, 1456), monitor, true), none), "title bar -> not a game");
                Contains(GameDetector.Describe(new ForegroundInfo("javaw", new Rectangle(0, 0, 800, 600), monitor, true), new[] { "javaw" }), "on the games list -> counts as a game");
                Equal("Nothing in front.", GameDetector.Describe(ForegroundInfo.None, none));
            });
            Run("taps: come from the config", () =>
            {
                Equal(2, TapSpec.From(Config.Parse("")).Count);
                var micOnly = TapSpec.From(Config.Parse("game_audio=off"));
                Equal(1, micOnly.Count); Equal("Mic", micOnly[0].Label); Equal("afftdn=nr=12:nf=-40", micOnly[0].Filter);
                True(!micOnly[0].Loopback, "mic is a capture device");
                True(TapSpec.From(Config.Parse("mic=off"))[0].Loopback, "game audio is loopback");
            });

            Run("clips: names parse into game, time and suffix", () =>
            {
                string game, suffix;
                DateTime taken;
                True(ClipLibrary.TryParseName("Rewind Fortnite 2026-09-08 18-20-32.mp4", out game, out taken, out suffix), "parses");
                Equal("Fortnite", game); Equal(new DateTime(2026, 9, 8, 18, 20, 32), taken); Equal("", suffix);
                True(ClipLibrary.TryParseName("Rewind 2026-09-08 18-20-32 trim 2.mp4", out game, out taken, out suffix), "no game");
                Equal("", game); Equal("trim 2", suffix);
                True(ClipLibrary.TryParseName("Rewind Rocket League 2026-09-08 18-20-32 trim.mp4", out game, out taken, out suffix), "spaces in the game");
                Equal("Rocket League", game); Equal("trim", suffix);
                True(!ClipLibrary.TryParseName("holiday.mp4", out game, out taken, out suffix), "other files");
            });
            Run("clips: describe falls back to the file time; title says Desktop", () =>
            {
                var when = new DateTime(2026, 1, 2, 3, 4, 5);
                var c = ClipLibrary.Describe(@"C:\v\holiday.mp4", 10, when);
                Equal(when, c.Taken); Equal("Desktop", c.Title); Equal("holiday.mp4", c.FileName); True(!c.Duration.HasValue, "unknown length");
                var named = ClipLibrary.Describe(@"C:\v\Rewind Fortnite 2026-09-08 18-20-32 trim.mp4", 10, when);
                Equal("Fortnite (trim)", named.Title); Equal(new DateTime(2026, 9, 8, 18, 20, 32), named.Taken);
                Equal(TimeSpan.FromSeconds(3), named.WithDuration(TimeSpan.FromSeconds(3)).Duration.Value);
            });
            Run("clips: copy name numbers itself past existing files", () =>
            {
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\v\a trim.mp4", @"C:\v\a trim 2.mp4" };
                Equal(@"C:\v\a trim 3.mp4", ClipLibrary.CopyName(@"C:\v\a.mp4", "trim", taken.Contains));
                Equal(@"C:\v\b trim.mp4", ClipLibrary.CopyName(@"C:\v\b.mp4", "trim", taken.Contains));
            });
            Run("probe: duration is read from ffmpeg's log", () =>
            {
                Equal(TimeSpan.FromSeconds(60.46), ClipProbe.ParseDuration("Input #0, mov\n  Duration: 00:01:00.46, start: 0.000000, bitrate: 19 kb/s").Value);
                Equal(TimeSpan.FromSeconds(3725.5), ClipProbe.ParseDuration("Duration: 01:02:05.50").Value);
                True(!ClipProbe.ParseDuration("Duration: N/A").HasValue, "n/a");
                True(!ClipProbe.ParseDuration("").HasValue, "empty");
            });
            Run("probe: thumb key follows the file", () =>
            {
                var t = new DateTime(2026, 1, 1);
                var a = ClipProbe.ThumbKey(@"C:\v\a.mp4", 10, t);
                Equal(a, ClipProbe.ThumbKey(@"c:\V\A.MP4", 10, t)); Equal(40, a.Length);
                True(a != ClipProbe.ThumbKey(@"C:\v\a.mp4", 11, t), "size changes it");
                True(a != ClipProbe.ThumbKey(@"C:\v\a.mp4", 10, t.AddSeconds(1)), "time changes it");
            });
            Run("probe: ffmpeg lines", () =>
            {
                Contains(ClipProbe.ThumbArgs(@"C:\v\a.mp4", @"C:\t\k.jpg"), "-ss 0.5 -i \"C:\\v\\a.mp4\" -frames:v 1 -vf scale=480:-2 -q:v 4 \"C:\\t\\k.jpg\"");
                Contains(ClipProbe.FrameArgs(@"C:\v\a.mp4", 12.345), "-ss 12.345 -i \"C:\\v\\a.mp4\" -frames:v 1 -vf scale=640:-2 -f image2pipe -c:v mjpeg -q:v 4 pipe:1");
            });
            Run("ffmpeg: trim re-encodes the video and copies the audio", () =>
            {
                var a = FfmpegArgs.Trim(@"C:\v\a.mp4", @"C:\v\a trim.mp4", 12.5, 8, Config.Parse("bitrate_mbps=20"));
                Contains(a, "-y -ss 12.5 -t 8 -i \"C:\\v\\a.mp4\" -map 0:v -map 0:a? -c:v h264_nvenc -preset p5 -tune hq -profile:v high -rc cbr -b:v 20M -maxrate 20M -bufsize 40M -c:a copy -movflags +faststart \"C:\\v\\a trim.mp4\"");
                True(!a.Contains("-g "), "no keyframe cadence on a trim");
                Contains(FfmpegArgs.Trim("a.mp4", "b.mp4", 0, 1, Config.Parse("codec=hevc")), "-tag:v hvc1 -c:a copy");
                Throws<ArgumentOutOfRangeException>(() => FfmpegArgs.Trim("a", "b", 0, 0, Config.Parse("")));
            });

            Run("shell: explorer gets /select with a quoted path", () =>
                Equal("/select,\"C:\\x\\a b.mp4\"", Shell.SelectInExplorerArgs(@"C:\x\a b.mp4")));

            Run("file name: process names are cleaned", () =>
            {
                Equal("Fortnite", ForegroundApp.Clean("FortniteClient-Win64-Shipping"));
                Equal("Minecraft", ForegroundApp.Clean("javaw"));
                Equal("weirdname", ForegroundApp.Clean("weird<>:name"));
                Equal("", ForegroundApp.Clean(""));
                Equal("explorer", ForegroundApp.Clean("explorer"));
                Equal("", ForegroundApp.Clean("Rewind")); // saved from Rewind's own window
                Equal(40, ForegroundApp.Clean(new string('a', 80)).Length);
            });

            Run("fault: ddagrab losing the screen is fatal, other ffmpeg chatter is not", () =>
            {
                True(CaptureFault.IsVideoLost("[Parsed_ddagrab_0 @ 0000013e41d81b00] AcquireNextFrame failed: 887a0026"), "access lost");
                True(CaptureFault.IsVideoLost("[in#0/lavfi @ 0000013e41d63f00] Error during demuxing: Generic error in an external library"), "lavfi demux error");
                True(!CaptureFault.IsVideoLost("[Parsed_ddagrab_0 @ 0000013e41d81b00] EOF timestamp not reliable"), "eof note");
                True(!CaptureFault.IsVideoLost("[aac @ 000001] Queue input is backward in time"), "audio warning");
                True(!CaptureFault.IsVideoLost(""), "empty");
                True(!CaptureFault.IsVideoLost(null), "null");
            });
            Run("fault: a buffer far too small for the bitrate is audio only", () =>
            {
                var minute = TimeSpan.FromSeconds(62);
                True(CaptureFault.LooksAudioOnly(3L * 1048576, minute, 20), "3 MB a minute at 20 Mbps");
                True(!CaptureFault.LooksAudioOnly(145L * 1048576, minute, 20), "healthy buffer");
                True(!CaptureFault.LooksAudioOnly(1L * 1048576, TimeSpan.FromSeconds(5), 20), "still filling");
                True(!CaptureFault.LooksAudioOnly(3L * 1048576, minute, 2), "low bitrate: audio alone is over the bar, stay quiet");
                Throws<ArgumentOutOfRangeException>(() => CaptureFault.LooksAudioOnly(-1, minute, 20));
                Throws<ArgumentOutOfRangeException>(() => CaptureFault.LooksAudioOnly(1, minute, 0));
            });

            // ---- the Medal features (2026-09-22) ----

            Run("config: medal-feature defaults", () =>
            {
                var c = Config.Parse("");
                Equal("ctrl+alt+r", c.HotkeyRecord); Equal("ctrl+alt+i", c.HotkeyScreenshot);
                Equal(120, c.RecordingMaxMinutes); Equal(20, c.ShareMaxMb); Equal(0, c.MaxStorageGb);
                True(c.Toast, "toast on"); True(c.Sound, "sound on"); Equal(80, c.SoundVolume);
                True(c.VoiceClip, "voice on"); Equal("clip that", c.VoicePhrase);
            });
            Run("config: any two hotkeys on one key are refused, off is allowed", () =>
            {
                Throws<ConfigException>(() => Config.Parse("hotkey_record=ctrl+alt+p"));
                Throws<ConfigException>(() => Config.Parse("hotkey_screenshot=Alt+Ctrl+O"));
                Throws<ConfigException>(() => Config.Parse("hotkey_record=F7\nhotkey_screenshot=f7"));
                Equal("", Config.Parse("hotkey_record=off").HotkeyRecord);
                Equal("", Config.Parse("hotkey_screenshot=").HotkeyScreenshot);
                Equal("F7", Config.Parse("hotkey_record=F7").HotkeyRecord);
            });
            Run("config: medal-feature ranges", () =>
            {
                Throws<ConfigException>(() => Config.Parse("sound_volume=101"));
                Throws<ConfigException>(() => Config.Parse("recording_max_minutes=0"));
                Throws<ConfigException>(() => Config.Parse("share_max_mb=0"));
                Throws<ConfigException>(() => Config.Parse("max_storage_gb=-1"));
                Throws<ConfigException>(() => Config.Parse("voice_phrase=" + new string('x', 41)));
                Throws<ConfigException>(() => Config.Parse("toast=sometimes"));
                Equal("clip that", Config.Parse("voice_phrase=   ").VoicePhrase);
                Equal(0, Config.Parse("sound_volume=0").SoundVolume);
            });
            Run("config: medal-feature values round-trip through text", () =>
            {
                var c = Config.Parse("hotkey_record=off\nhotkey_screenshot=F12\nrecording_max_minutes=30\nshare_max_mb=50\nmax_storage_gb=200\ntoast=off\nsound=off\nsound_volume=35\nvoice_clip=off\nvoice_phrase=save that");
                var back = Config.Parse(c.Text());
                Equal("", back.HotkeyRecord); Equal("F12", back.HotkeyScreenshot); Equal(30, back.RecordingMaxMinutes); Equal(50, back.ShareMaxMb);
                Equal(200, back.MaxStorageGb); True(!back.Toast && !back.Sound && !back.VoiceClip, "all off"); Equal(35, back.SoundVolume); Equal("save that", back.VoicePhrase);
                Contains(c.Text(), "hotkey_record=off" + Environment.NewLine);
            });

            Run("tscut: PlanLast takes the newest keyframe and the newest tables before it", () =>
            {
                var key = TsPacket(256, true, true, Payload(1));
                var rest = TsPacket(256, false, false, Payload(2));
                var pat2 = TsPacket(0, true, false, Pat(4096));
                var all = Join(pat, pmt, key, rest, pat2, pmt, key, rest);
                var first = TsCut.Plan(Split(all, 500));
                var last = TsCut.PlanLast(Split(all, 500));
                Equal(188L * 2, first.TrimmedBytes);
                Equal(188L * 6, last.TrimmedBytes);
                True(last.Clean, "clean");
                Equal(BitConverter.ToString(Join(pat2, pmt)), BitConverter.ToString(last.Prefix));
                True(!TsCut.PlanLast(Split(Join(pat, pmt, rest, rest), 300)).Clean, "no keyframe = raw");
            });

            Run("screenshot: the last complete BMP in a stream, torn tails ignored", () =>
            {
                var a = Bmp(200, 0xAA);
                var b = Bmp(300, 0xBB);
                var torn = new byte[100];
                Buffer.BlockCopy(Bmp(500, 0xCC), 0, torn, 0, 100);
                Equal(BitConverter.ToString(b), BitConverter.ToString(Screenshot.LastBmp(new MemoryStream(Join(a, b, torn)))));
                Equal(BitConverter.ToString(a), BitConverter.ToString(Screenshot.LastBmp(new MemoryStream(a))));
                True(Screenshot.LastBmp(new MemoryStream(new byte[0])) == null, "empty");
                True(Screenshot.LastBmp(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 })) == null, "garbage");
            });

            Run("share plan: fits as it is under the limit", () =>
            {
                var p = SharePlan.For(19000000, 60, 20, 1440, 2);
                True(!p.NeedsEncode, "no encode"); Equal(20000000L, p.LimitBytes); Equal("fits as it is", p.ToString());
            });
            Run("share plan: a minute at 150 MB into 20 MB is 720p30 at ~2.4 Mbps", () =>
            {
                var p = SharePlan.For(150000000, 60, 20, 1440, 2);
                True(p.NeedsEncode, "encode"); Equal(96, p.AudioKbps); Equal(2357, p.VideoKbps); Equal(720, p.Height); Equal(30, p.Fps);
                Contains(p.ToString(), "2357 kbps video + 96 kbps audio, 720p, 30 fps");
            });
            Run("share plan: ten seconds keeps 1080p and full fps; a 720p source is not upscaled; no audio = no audio bits", () =>
            {
                var p = SharePlan.For(36000000, 10, 20, 1440, 2);
                Equal(14624, p.VideoKbps); Equal(1080, p.Height); Equal(0, p.Fps);
                Equal(0, SharePlan.For(36000000, 10, 20, 720, 2).Height);
                Equal(0, SharePlan.For(36000000, 10, 20, 1440, 0).AudioKbps);
                Equal(14720, SharePlan.For(36000000, 10, 20, 1440, 0).VideoKbps);
            });
            Run("share plan: shrunk scales the bitrate by how far over it landed", () =>
            {
                var p = SharePlan.For(36000000, 10, 20, 1440, 2);
                var s = p.Shrunk(22000000);
                True(s.VideoKbps < p.VideoKbps, "smaller"); Equal(12629, s.VideoKbps); Equal(1080, s.Height);
                Equal(250, SharePlan.For(600000000, 600, 1, 1440, 2).VideoKbps);
                Throws<ArgumentOutOfRangeException>(() => SharePlan.For(1, 0, 20, 1440, 2));
            });

            Run("storage: oldest go first until it fits; the newest always stays; 0 = never", () =>
            {
                var t0 = new DateTime(2026, 9, 1);
                var mb = 1048576L;
                var clips = new List<ClipInfo>
                {
                    new ClipInfo(@"C:\v\b.mp4", "", "", t0.AddDays(1), 100 * mb, null),
                    new ClipInfo(@"C:\v\a.mp4", "", "", t0, 100 * mb, null),
                    new ClipInfo(@"C:\v\c.png", "", "", t0.AddDays(2), 100 * mb, null)
                };
                var doomed = StoragePolicy.ToDelete(clips, 150 * mb);
                Equal(2, doomed.Count); Equal(@"C:\v\a.mp4", doomed[0].Path); Equal(@"C:\v\b.mp4", doomed[1].Path);
                Equal(1, StoragePolicy.ToDelete(clips, 250 * mb).Count);
                Equal(0, StoragePolicy.ToDelete(clips, 300 * mb).Count);
                Equal(0, StoragePolicy.ToDelete(clips, 0).Count);
                Equal(2, StoragePolicy.ToDelete(clips, 1).Count); // even a cap below one clip keeps the newest
                Equal(0, StoragePolicy.ToDelete(new List<ClipInfo> { clips[0] }, 1).Count);
            });

            Run("ffmpeg: recording remux reads the .ts file, no faststart", () =>
            {
                var a = FfmpegArgs.RemuxFile(@"C:\v\r.ts", @"C:\v\r.mp4", new[] { "Game", "Mic" }, "h264");
                Equal("-hide_banner -loglevel error -nostdin -y -f mpegts -i \"C:\\v\\r.ts\" -map 0:v -map 0:a? -c copy -metadata:s:a:0 handler_name=\"Game\" -metadata:s:a:1 handler_name=\"Mic\" \"C:\\v\\r.mp4\"", a);
                True(!a.Contains("faststart"), "no faststart");
                Contains(FfmpegArgs.RemuxFile("a.ts", "b.mp4", new string[0], "hevc"), "-c copy -tag:v hvc1 \"b.mp4\"");
            });
            Run("ffmpeg: screenshot line decodes stdin to BMPs on stdout", () =>
                Equal("-hide_banner -loglevel error -nostdin -f mpegts -i pipe:0 -map 0:v -an -fps_mode passthrough -f image2pipe -c:v bmp pipe:1", FfmpegArgs.Screenshot()));
            Run("ffmpeg: share line mixes the tracks and scales", () =>
            {
                var plan = SharePlan.For(150000000, 60, 20, 1440, 2);
                var a = FfmpegArgs.Share(@"C:\v\a.mp4", @"C:\v\a share.mp4", plan, 2);
                Contains(a, "-y -i \"C:\\v\\a.mp4\" -map 0:v -filter_complex \"[0:a:0][0:a:1]amix=inputs=2:duration=first:normalize=0[a]\" -map \"[a]\" -vf \"scale=-2:720,fps=30\" -c:v h264_nvenc -preset p5 -tune hq -profile:v high -rc cbr -b:v 2357k -maxrate 2357k -bufsize 4714k -pix_fmt yuv420p -c:a aac -b:a 96k -ac 2 -movflags +faststart \"C:\\v\\a share.mp4\"");
                var one = FfmpegArgs.Share("a.mp4", "b.mp4", SharePlan.For(36000000, 10, 20, 1440, 1), 1);
                Contains(one, "-map 0:v -map 0:a:0 -vf \"scale=-2:1080\" -c:v h264_nvenc");
                True(!one.Contains("fps="), "full fps kept");
                var silent = FfmpegArgs.Share("a.mp4", "b.mp4", SharePlan.For(36000000, 10, 20, 720, 0), 0);
                Contains(silent, "-map 0:v -an -c:v h264_nvenc");
                True(!silent.Contains("-vf") && !silent.Contains("-c:a"), "no scale, no audio");
                Throws<ArgumentException>(() => FfmpegArgs.Share("a", "b", SharePlan.For(1, 1, 20, 720, 0), 0));
            });
            Run("ffmpeg: gif line does palette and use in one run", () =>
            {
                var a = FfmpegArgs.Gif(@"C:\v\a.mp4", @"C:\v\a.gif", 2.5, 3);
                Equal("-hide_banner -loglevel error -nostdin -y -ss 2.5 -t 3 -i \"C:\\v\\a.mp4\" -an -filter_complex \"fps=15,scale=480:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle\" -loop 0 \"C:\\v\\a.gif\"", a);
                Throws<ArgumentOutOfRangeException>(() => FfmpegArgs.Gif("a", "b", -1, 3));
            });

            Run("clips: every file Rewind writes is named the same way", () =>
            {
                var when = new DateTime(2026, 9, 22, 18, 5, 9);
                Equal("Rewind Fortnite 2026-09-22 18-05-09.mp4", ClipLibrary.NewName("FortniteClient-Win64-Shipping", when, "", "mp4"));
                Equal("Rewind 2026-09-22 18-05-09.png", ClipLibrary.NewName("", when, "", "png"));
                Equal("Rewind Minecraft 2026-09-22 18-05-09 recording.ts", ClipLibrary.NewName("javaw", when, "recording", "ts"));
                Equal("Rewind 2026-09-22 18-05-09 recording 2.ts", ClipLibrary.NewName("Rewind", when, "recording 2", "ts"));
            });
            Run("clips: screenshots and recordings parse and show as such", () =>
            {
                string game, suffix;
                DateTime taken;
                True(ClipLibrary.TryParseName("Rewind Fortnite 2026-09-22 18-05-09.png", out game, out taken, out suffix), "png parses");
                Equal("Fortnite", game); Equal("", suffix);
                var shot = ClipLibrary.Describe(@"C:\v\Rewind Fortnite 2026-09-22 18-05-09.png", 10, DateTime.Now);
                True(shot.IsImage, "is image"); Equal("Fortnite (screenshot)", shot.Title);
                var rec = ClipLibrary.Describe(@"C:\v\Rewind 2026-09-22 18-05-09 recording.mp4", 10, DateTime.Now);
                True(!rec.IsImage, "video"); Equal("Desktop (recording)", rec.Title);
                True(!ClipLibrary.TryParseName("Rewind 2026-09-22 18-05-09 recording.ts", out game, out taken, out suffix), ".ts is not a clip");
                Equal(@"C:\v\a.gif", ClipLibrary.CopyName(@"C:\v\a.mp4", "", "gif", p => false));
                Equal(@"C:\v\a 2.gif", ClipLibrary.CopyName(@"C:\v\a.mp4", "", "gif", p => p == @"C:\v\a.gif"));
            });
            Run("probe: height, audio tracks and the inspect line", () =>
            {
                var output = "Input #0, mov,mp4\n  Duration: 00:00:15.90, start: 0.000000, bitrate: 19 kb/s\n"
                    + "  Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, progressive), 2560x1440 [SAR 1:1 DAR 16:9], 18888 kb/s, 60 fps\n"
                    + "  Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 191 kb/s\n"
                    + "  Stream #0:2[0x3](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, mono, fltp, 69 kb/s\n";
                Equal(1440, ClipProbe.ParseHeight(output)); Equal(2, ClipProbe.ParseAudioTracks(output));
                Equal(0, ClipProbe.ParseHeight("nothing")); Equal(0, ClipProbe.ParseAudioTracks(""));
                Equal("-hide_banner -nostdin -i \"C:\\v\\a.mp4\"", ClipProbe.InspectArgs(@"C:\v\a.mp4"));
                Contains(ClipProbe.ThumbArgs(@"C:\v\a.png", @"C:\t\k.jpg", true), "-y -i \"C:\\v\\a.png\" -frames:v 1");
                True(!ClipProbe.ThumbArgs(@"C:\v\a.png", @"C:\t\k.jpg", true).Contains("-ss"), "no seek into a still");
            });

            Run("sounds: a real WAV comes out, silent at volume 0, different per chime", () =>
            {
                var wav = Sounds.Wav(Chime.Clip, 80);
                Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4)); Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
                Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4)); Equal(wav.Length - 44, BitConverter.ToInt32(wav, 40));
                Equal(Sounds.SampleRate, BitConverter.ToInt32(wav, 24)); Equal((short)16, BitConverter.ToInt16(wav, 34));
                True(wav.Length > 44 + Sounds.SampleRate / 2, "at least a quarter second");
                var loud = 0;
                for (var i = 44; i < wav.Length; i += 2) if (Math.Abs(BitConverter.ToInt16(wav, i)) > 8000) loud++;
                True(loud > 1000, "has sound in it");
                var quiet = Sounds.Wav(Chime.Clip, 0);
                for (var i = 44; i < quiet.Length; i += 2) if (BitConverter.ToInt16(quiet, i) != 0) throw new Exception("volume 0 should be silent");
                True(Sounds.Wav(Chime.Screenshot, 80).Length != wav.Length || BitConverter.ToString(Sounds.Wav(Chime.Screenshot, 80)) != BitConverter.ToString(wav), "chimes differ");
                Equal("record-stop.wav", Sounds.FileName(Chime.RecordStop)); Equal("clip.wav", Sounds.FileName(Chime.Clip));
                Throws<ArgumentOutOfRangeException>(() => Sounds.Wav(Chime.Clip, 101));
            });

            Run("config: voice engine and url", () =>
            {
                var c = Config.Parse("");
                Equal("windows", c.VoiceEngine); Equal("wss://thoughts.riomax.com/ws/transcribe", c.VoiceUrl);
                Equal("riovoice", Config.Parse("voice_engine=RioVoice").VoiceEngine);
                Equal("ws://10.0.0.5:9100/ws/transcribe", Config.Parse("voice_url=ws://10.0.0.5:9100/ws/transcribe").VoiceUrl);
                Equal(c.VoiceUrl, Config.Parse("voice_url=").VoiceUrl);
                Throws<ConfigException>(() => Config.Parse("voice_engine=siri"));
                Throws<ConfigException>(() => Config.Parse("voice_url=https://example.com"));
                Contains(Config.Parse("voice_engine=riovoice").Text(), "voice_engine=riovoice" + Environment.NewLine);
            });

            Run("speech: 48 kHz stereo float becomes 16 kHz mono int16", () =>
            {
                var format = new PcmFormat("f32le", 48000, 2, 8);
                var raw = new byte[8 * 480]; // 10 ms of stereo float
                for (var f = 0; f < 480; f++)
                {
                    Buffer.BlockCopy(BitConverter.GetBytes(0.5f), 0, raw, f * 8, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(-0.5f), 0, raw, f * 8 + 4, 4);
                }
                var mono = SpeechGate.ToMono(raw, raw.Length, format);
                Equal(480, mono.Length); Equal(0f, mono[10]);
                var loud = new float[480];
                for (var i = 0; i < loud.Length; i++) loud[i] = 0.25f;
                var pcm = SpeechGate.ToPcm16k(loud, 48000);
                Equal(160 * 2, pcm.Length); Equal((short)8192, BitConverter.ToInt16(pcm, 100));
                Equal(0, SpeechGate.ToPcm16k(new float[0], 48000).Length);
                True(Math.Abs(SpeechGate.Rms(loud) - 0.25) < 0.0001, "rms");
                var s16 = new PcmFormat("s16le", 16000, 1, 2);
                Equal(1f, SpeechGate.ToMono(new byte[] { 0xff, 0x7f }, 2, s16)[0] > 0.99f ? 1f : 0f);
            });
            Run("speech: the phrase is found once, whatever the punctuation", () =>
            {
                int upTo;
                True(SpeechGate.Heard("Okay, CLIP THAT!", "clip that", 0, out upTo), "found"); Equal(16, upTo);
                True(!SpeechGate.Heard("Okay, CLIP THAT!", "clip that", upTo, out upTo), "not twice");
                True(SpeechGate.Heard("Okay, clip that. Nice. Clip that again", "clip that", 16, out upTo), "again later");
                True(!SpeechGate.Heard("the clipthat", "clip that", 0, out upTo), "no glued words");
                True(!SpeechGate.Heard("clip", "clip that", 0, out upTo), "half is not enough");
                True(!SpeechGate.Heard("", "clip that", 0, out upTo), "empty");
                Equal("clip that", SpeechGate.Normalize("  Clip,  THAT! "));
            });
            Run("speech: the ASR reply is read", () =>
            {
                Equal("clip that", SpeechGate.ParseText("{\"text\": \"clip that\", \"final\": false, \"capped\": false}"));
                Equal("he said \"go\"", SpeechGate.ParseText("{\"text\": \"he said \\\"go\\\"\", \"final\": true}"));
                Equal("", SpeechGate.ParseText("{\"text\": \"\", \"final\": true}"));
                True(SpeechGate.ParseText("{\"detail\": \"nope\"}") == null, "no text field");
                True(SpeechGate.IsFinal("{\"text\": \"x\", \"final\": true}"), "final");
                True(!SpeechGate.IsFinal("{\"text\": \"x\", \"final\": false, \"capped\": false}"), "not final");
            });
            Run("speech: the gate opens on a loud slice and closes after quiet or the maximum", () =>
            {
                var gate = new VoiceActivity(3, 10);
                for (var i = 0; i < 50; i++) Equal(GateEvent.Quiet, gate.Feed(0.002));
                True(gate.RoomLevel < 0.01, "room level learned low");
                Equal(GateEvent.Started, gate.Feed(0.2));
                Equal(GateEvent.Speaking, gate.Feed(0.15));
                Equal(GateEvent.Speaking, gate.Feed(0.001));
                Equal(GateEvent.Speaking, gate.Feed(0.001));
                Equal(GateEvent.Ended, gate.Feed(0.001));
                Equal(GateEvent.Started, gate.Feed(0.3));
                for (var i = 0; i < 8; i++) Equal(GateEvent.Speaking, gate.Feed(0.3));
                Equal(GateEvent.Ended, gate.Feed(0.3)); // ten slices: cut
                True(!gate.Speaking, "closed");
                Equal(GateEvent.Quiet, gate.Feed(0.006)); // under the floor: never speech
                Throws<ArgumentOutOfRangeException>(() => new VoiceActivity(0, 1));
            });
            UpdateTests();

            Console.WriteLine();
            Console.WriteLine(string.Format("{0} passed, {1} failed", _passed, _failed));
            return _failed == 0 ? 0 : 1;
        }

        /// <summary>A fake BMP: the 'BM' magic, the file size, then filler.</summary>
        private static byte[] Bmp(int size, byte fill)
        {
            var bytes = new byte[size];
            for (var i = 0; i < size; i++) bytes[i] = fill;
            bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
            bytes[2] = (byte)size; bytes[3] = (byte)(size >> 8); bytes[4] = (byte)(size >> 16); bytes[5] = (byte)(size >> 24);
            return bytes;
        }

        // ---- synthetic MPEG-TS packets for the TsCut tests ----

        /// <summary>A 188-byte packet with an adaptation field (carrying the RAI flag) and a short payload.</summary>
        private static byte[] TsPacket(int pid, bool pusi, bool rai, byte[] payload)
        {
            if (payload.Length > 182) throw new ArgumentException("payload too long for one packet");
            var packet = new byte[188];
            packet[0] = 0x47;
            packet[1] = (byte)((pusi ? 0x40 : 0) | (pid >> 8));
            packet[2] = (byte)(pid & 0xff);
            packet[3] = 0x30; // adaptation field + payload
            var afLength = 188 - 4 - 1 - payload.Length;
            packet[4] = (byte)afLength;
            packet[5] = (byte)(rai ? 0x40 : 0x00);
            for (var i = 6; i < 5 + afLength; i++) packet[i] = 0xff;
            Buffer.BlockCopy(payload, 0, packet, 5 + afLength, payload.Length);
            return packet;
        }

        private static byte[] Payload(byte fill)
        {
            var bytes = new byte[100];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = fill;
            return bytes;
        }

        private static byte[] Pat(int pmtPid)
        {
            return new byte[]
            {
                0x00,                   // pointer field
                0x00, 0xB0, 0x0D,       // table id, section length 13
                0x00, 0x01, 0xC1, 0x00, 0x00,
                0x00, 0x01, (byte)(0xE0 | (pmtPid >> 8)), (byte)(pmtPid & 0xff),
                0, 0, 0, 0              // crc (not checked)
            };
        }

        private static byte[] Pmt(byte[] types, int[] pids)
        {
            var section = new List<byte> { 0x00, 0x02, 0x00, 0x00, 0x00, 0x01, 0xC1, 0x00, 0x00, 0xE1, 0x00, 0xF0, 0x00 };
            for (var i = 0; i < types.Length; i++)
                section.AddRange(new[] { types[i], (byte)(0xE0 | (pids[i] >> 8)), (byte)(pids[i] & 0xff), (byte)0xF0, (byte)0x00 });
            section.AddRange(new byte[] { 0, 0, 0, 0 });
            var length = section.Count - 4; // everything after the length field
            section[2] = (byte)(0xB0 | (length >> 8));
            section[3] = (byte)(length & 0xff);
            return section.ToArray();
        }

        private static byte[] Join(params byte[][] parts)
        {
            var total = 0;
            foreach (var part in parts) total += part.Length;
            var all = new byte[total];
            var at = 0;
            foreach (var part in parts) { Buffer.BlockCopy(part, 0, all, at, part.Length); at += part.Length; }
            return all;
        }

        /// <summary>Chops bytes into chunks of the given sizes; the last chunk takes whatever is left.</summary>
        private static IList<Chunk> Split(byte[] all, params int[] sizes)
        {
            var chunks = new List<Chunk>();
            var at = 0;
            foreach (var size in sizes)
            {
                var take = Math.Min(size, all.Length - at);
                if (take <= 0) break;
                var data = new byte[take];
                Buffer.BlockCopy(all, at, data, 0, take);
                chunks.Add(new Chunk(DateTime.UtcNow, data));
                at += take;
            }
            if (at < all.Length)
            {
                var data = new byte[all.Length - at];
                Buffer.BlockCopy(all, at, data, 0, data.Length);
                chunks.Add(new Chunk(DateTime.UtcNow, data));
            }
            return chunks;
        }

        // ---- the tiniest test harness ----

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine("  ok   " + name);
            }
            catch (Exception error)
            {
                _failed++;
                Console.WriteLine("  FAIL " + name + ": " + error.Message);
            }
        }

        private static void True(bool condition, string what)
        {
            if (!condition) throw new Exception("expected " + what);
        }

        private static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception(string.Format("expected [{0}] but got [{1}]", expected, actual));
        }

        private static void Contains(string haystack, string needle)
        {
            if (haystack.IndexOf(needle, StringComparison.Ordinal) < 0)
                throw new Exception("expected to find [" + needle + "] in [" + haystack + "]");
        }

        private static int Count(string haystack, string needle)
        {
            int count = 0, at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
            return count;
        }

        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            catch (Exception other) { throw new Exception("expected " + typeof(T).Name + " but got " + other.GetType().Name + ": " + other.Message); }
            throw new Exception("expected " + typeof(T).Name + " but nothing was thrown");
        }
    }
}
