# Rewind

A replay buffer for the main monitor, like Medal's clip button without the rest of Medal.
It sits in the tray, quietly keeps the last 60 seconds of the primary monitor in memory
(video from the GPU, game audio and mic as separate tracks), and **Ctrl+Alt+P** writes those
60 seconds to an MP4 in `Videos\Rewind`. **Ctrl+Alt+O** saves just the last 15 seconds.

## Use
- **Ctrl+Alt+P** (or double-click the tray dot) saves a clip; **Ctrl+Alt+O** saves a short one.
  A balloon and a ding confirm it; click the balloon to see the file in its folder.
- **The window** (tray → *Open Rewind*, or just run `Rewind.exe` again): every clip as a thumbnail,
  newest first, grouped by day, with a filter per game. Double-click plays it; the buttons underneath
  show it in the folder, rename it, delete it (to the Recycle Bin, two taps) or **trim** it — pick a
  start and an end on the sliders, watch the frame under the handle, save the stretch as a new file
  (the original stays). The strip at the top shows what Rewind is doing right now with Save / Pause
  buttons, and the **Settings** tab edits every setting with an Apply button.
- Right-click the dot: open the window, save, pause/resume, open the clips folder, open/reload
  `config.txt`, open the log, quit.
- `Rewind.exe --save` / `--save-short` save a clip from anywhere (a Stream Deck button, a script),
  `--clips` opens the window, `--quit` stops it, `--list` shows the monitors and audio devices it can
  see and whether the app in front counts as a game.
- Settings live in `config.txt` next to the exe: hotkeys, seconds, fps, bitrate, codec, monitor,
  audio, games-only recording, clips folder. The Settings tab writes the same file.

## How it works
One `ffmpeg` process does the heavy lifting, all on the graphics card:
`ddagrab` (Windows Desktop Duplication) grabs the monitor as D3D11 textures, `h264_nvenc` encodes
them on the RTX's hardware encoder, and the result streams out as MPEG-TS into Rewind's memory ring
(`ChunkRing`). MPEG-TS can be cut at any byte and still play, so saving a clip is "find the first
keyframe in the last N seconds (`TsCut` — ffmpeg marks them in the stream), then pour the bytes from
there straight into an ffmpeg that wraps them in an MP4" — no re-encoding, no temp file, no copy of
the buffer, about a second.

Audio comes from two WASAPI taps (`AudioTap`): a loopback on the default output (what the headset
hears) and the default microphone. Each feeds ffmpeg over a named pipe at exactly the device's
sample rate against wall-clock, padding silence when nothing is playing, which is what keeps sound
and picture in step. A device that isn't there at start (headset still off) is retried every 15 s
and picked up when it appears.

If ffmpeg dies (monitor re-plug, lock screen, driver hiccup) `Recorder` restarts it with backoff.
Locking the PC pauses capture; unlocking resumes it. With `record=games`, capture only runs while a
game is in front (anything on the `games=` list, or any app covering its monitor with no title bar)
and pauses once one has been gone for `game_grace_seconds`; the tray dot turns amber while waiting.

## Just want the .exe?
Download **`Rewind.exe`** from the [latest release](https://github.com/Rio-Kamran/rewind/releases/latest)
and run it. You need Windows 10/11 and an **NVIDIA** graphics card (the encoding runs on its NVENC chip).
The first time, it downloads ffmpeg by itself (about 110 MB, once, into `%LOCALAPPDATA%\Rewind\ffmpeg`)
and then starts recording — the tray dot turns red when it's ready and a balloon says so. `config.txt`
appears next to the exe. To start with Windows: Win+R → `shell:startup` → drop in a shortcut to Rewind.exe.

## Building it yourself
Same requirements (Windows 10/11, NVIDIA card). AMD/Intel would need a different encoder, see below.

1. Install ffmpeg (or skip this — Rewind fetches one on first run). The build must have `ddagrab` and
   `h264_nvenc` in it — this one does:
   ```
   winget install yt-dlp.FFmpeg
   ```
   (open a new terminal afterwards so `ffmpeg` is on PATH; `ffmpeg -filters | findstr ddagrab` should print a line).
2. Get the code and build it — no Visual Studio needed, the compiler is already inside Windows:
   ```
   git clone https://github.com/Rio-Kamran/rewind
   cd rewind
   powershell -ExecutionPolicy Bypass -File build.ps1
   ```
   You should see `Building Rewind.exe` and `NN passed, 0 failed`.
3. Run `Rewind.exe`. A red dot appears in the tray and it is already recording. Press **Ctrl+Alt+P**
   to save the last 60 s; clips land in `Videos\Rewind`. Right-click the dot → *Open Rewind* for the
   clip window and the settings.
4. To have it start with Windows: press Win+R, type `shell:startup`, and put a shortcut to `Rewind.exe`
   in the folder that opens.

Settings are in `config.txt` (created next to the exe on first run from `config.example.txt`) or in the
window's Settings tab. Non-NVIDIA cards: change `codec` handling in `src/FfmpegArgs.cs` to `h264_amf`
(AMD) or `h264_qsv` (Intel) — untested.

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
| `src/Program.cs` | entry point, `--save` / `--save-short` / `--clips` / `--quit` / `--list`, single-instance guard |
| `src/TrayApp.cs` | tray icon and menu, hotkeys, balloons, lock/unlock, the game watch, the audio-device retry, the window |
| `src/CaptureSession.cs` | the pipeline as one thing: ring + taps + recorder + the three pause reasons; rebuilds on a config change |
| `src/Recorder.cs` | keeps the ffmpeg capture alive, feeds the ring, restarts on failure |
| `src/AudioTap.cs` | WASAPI loopback / mic capture, paced named-pipe writer |
| `src/ChunkRing.cs` | the in-memory replay buffer |
| `src/TsCut.cs` | finds the first keyframe (and the PAT/PMT tables before it) in the ring, so a clip starts clean |
| `src/ClipSaver.cs` | ring -> ffmpeg stdin -> MP4 remux; names the clip after the game in front |
| `src/FfmpegArgs.cs` | builds the ffmpeg command lines: capture, remux, trim (pure, tested) |
| `src/Config.cs` | `config.txt` parsing, validation, and writing it back with comments |
| `src/GameDetector.cs` / `ForegroundApp.cs` | what's in front and whether it counts as a game (pure, tested) |
| `src/ClipsForm.cs` / `ClipGrid.cs` / `SettingsTab.cs` / `TrimForm.cs` | the window: the form, the owner-painted grid (tiles fill the width), settings editor, trim dialog |
| `src/ClipLibrary.cs` / `ClipProbe.cs` | listing clips and reading names; thumbnails, durations and preview frames via ffmpeg |
| `src/HotkeySpec.cs` / `HotkeyWindow.cs` | hotkey parsing and the global RegisterHotKey window (two slots) |
| `src/Dxgi.cs` | finds which DXGI output is the primary monitor |
| `src/CoreAudio.cs` | the WASAPI COM interfaces |
| `src/RewindControl.cs` / `Shell.cs` / `KillOnCloseJob.cs` / `Log.cs` / `FfmpegLocator.cs` | the window's seam to the tray app, explorer args, the ffmpeg kill-on-close job, the log, finding ffmpeg |
| `tests/Tests.cs` | unit tests (no framework needed) |
