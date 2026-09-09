# Rewind

A replay buffer for the main monitor, like Medal's clip button without the rest of Medal.
It sits in the tray, quietly keeps the last 60 seconds of the primary monitor in memory
(video from the GPU, game audio and mic as separate tracks), and **Ctrl+Alt+P** writes those
60 seconds to an MP4 in `Videos\Rewind`.

## Use
- **Ctrl+Alt+P** (or double-click the red tray dot) saves a clip. A balloon and a ding confirm it.
- Right-click the dot: save, pause/resume, open the clips folder, open/reload `config.txt`, open the log, quit.
- `Rewind.exe --save` saves a clip from anywhere (a Stream Deck button, a script).
- `Rewind.exe --list` shows the monitors and audio devices it can see.
- Settings live in `config.txt` next to the exe (hotkey, seconds, fps, bitrate, codec, monitor, mic on/off).

## How it works
One `ffmpeg` process does the heavy lifting, all on the graphics card:
`ddagrab` (Windows Desktop Duplication) grabs the monitor as D3D11 textures, `h264_nvenc` encodes
them on the RTX's hardware encoder, and the result streams out as MPEG-TS into Rewind's memory ring
(`ChunkRing`). MPEG-TS can be cut at any byte and still play, so saving a clip is just "write the
last N seconds of bytes to a file and let ffmpeg wrap them in an MP4" - no re-encoding, about a second.

Audio comes from two WASAPI taps (`AudioTap`): a loopback on the default output (what the headset
hears) and the default microphone. Each feeds ffmpeg over a named pipe at exactly the device's
sample rate against wall-clock, padding silence when nothing is playing, which is what keeps sound
and picture in step.

If ffmpeg dies (monitor re-plug, lock screen, driver hiccup) `Recorder` restarts it with backoff.
Locking the PC pauses capture; unlocking resumes it.

## Build
```
powershell -File build.ps1
```
Uses the C# compiler that ships inside Windows (`csc.exe` in `Microsoft.NET\Framework64\v4.0.30319`),
no Visual Studio needed. Builds `Rewind.exe`, then `rewind-tests.exe`, and runs the tests.
Needs `ffmpeg.exe` on PATH (a recent build with `ddagrab` and `h264_nvenc`; the yt-dlp winget one works).

## Files
| File | Job |
|---|---|
| `src/Program.cs` | entry point, `--save` / `--list`, single-instance guard |
| `src/TrayApp.cs` | tray icon and menu, hotkey wiring, balloons, lock/unlock pause |
| `src/Recorder.cs` | keeps the ffmpeg capture alive, feeds the ring, restarts on failure |
| `src/AudioTap.cs` | WASAPI loopback / mic capture, paced named-pipe writer |
| `src/ChunkRing.cs` | the in-memory replay buffer |
| `src/ClipSaver.cs` | ring -> temp .ts -> MP4 remux; names the clip after the game in front |
| `src/FfmpegArgs.cs` | builds the ffmpeg command lines (pure, tested) |
| `src/Config.cs` | `config.txt` parsing and validation |
| `src/HotkeySpec.cs` / `HotkeyWindow.cs` | hotkey parsing and the global RegisterHotKey window |
| `src/Dxgi.cs` | finds which DXGI output is the primary monitor |
| `src/CoreAudio.cs` | the WASAPI COM interfaces |
| `tests/Tests.cs` | unit tests (no framework needed) |
