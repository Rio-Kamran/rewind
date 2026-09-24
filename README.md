# Rewind

A replay buffer for the main monitor, like Medal's clip button without the rest of Medal.
It sits in the tray, quietly keeps the last 60 seconds of the primary monitor in memory
(video from the GPU, game audio and mic as separate tracks), and **Ctrl+Alt+P** writes those
60 seconds to an MP4 in `Videos\Rewind`. **Ctrl+Alt+O** saves just the last 15 seconds.
Say **"clip that"** into the mic and it does the same. **Ctrl+Alt+R** starts a long recording,
**Ctrl+Alt+I** takes a screenshot, and the window has a **Copy for Discord** button that shrinks
any clip under Discord's upload limit and puts it on the clipboard.

## Use
- **Ctrl+Alt+P** (or double-click the tray dot) saves a clip; **Ctrl+Alt+O** saves a short one.
  A chime and a small card in the corner of the screen confirm it (the card shows over most games);
  click the card to see the file in its folder. **"Clip that"**, said into the mic, saves a clip too.
- **Ctrl+Alt+R** starts a **long recording** (Medal's full-session recording): press it again to stop,
  and the whole stretch lands as an MP4 next to the clips. It stops itself after two hours. Clips and
  screenshots still work while it runs. A small `REC 12:34` card stays in the corner so you can't forget.
- **Ctrl+Alt+I** saves a **screenshot** of the main monitor as a PNG and copies it, so Ctrl+V pastes
  it straight into Discord. Screenshots show up in the window with the clips.
- **The window** (tray → *Open Rewind*, or just run `Rewind.exe` again): every clip and screenshot as a
  thumbnail, newest first, grouped by day, with a filter per game. Double-click plays it; the buttons
  underneath show it in the folder, rename it, delete it (to the Recycle Bin, two taps), **trim** it
  (pick a start and an end on the sliders, watch the frame under the handle, save the stretch as a new
  file or as a looping **GIF**), or **Copy for Discord**: if the clip is under 20 MB it goes on the
  clipboard as it is, otherwise a shrunk copy is made first (1080p/720p, game and mic mixed together)
  and that goes on the clipboard. Paste into Discord with Ctrl+V. The strip at the top shows what Rewind
  is doing right now with Save / Screenshot / Record / Pause buttons, and the **Settings** tab edits
  every setting with an Apply button, plus "Start with Windows" and a "Test sound" button.
- Right-click the dot: open the window, save, *Save last…* (15 s / 30 s / everything), screenshot,
  start/stop recording, pause/resume, open the clips folder, open/reload `config.txt`, open the log, quit.
- `Rewind.exe --save` / `--save-short` / `--screenshot` / `--record` do those from anywhere (a Stream Deck
  button, a script), `--clips` opens the window, `--quit` stops it, `--list` shows the monitors and audio
  devices it can see and whether the app in front counts as a game.
- Settings live in `config.txt` next to the exe: hotkeys, seconds, fps, bitrate, codec, monitor,
  audio, games-only recording, the recording limit, the Discord size limit, a storage cap (delete the
  oldest clips past N GB), the chime and its volume, the voice phrase, clips folder. The Settings tab
  writes the same file. Drop a `clip.wav` next to the exe to use your own chime.
- `mic_filter` takes any ffmpeg audio filter chain. The default is a plain FFT denoiser; if you have an RNNoise
  model file (`.rnnn`, the `rnnoise-nu` text format, e.g. from the rnnoise-models project) put it in a `rnnoise\`
  folder next to the exe and use `mic_filter=arnndn=m=rnnoise/your-model.rnnn` (ffmpeg runs from the exe folder).
  `highpass=f=80,arnndn=m=…,speechnorm=e=6:r=0.0005:l=1` also evens out the voice level. Costs well under 1 % of a core.

## How it works
One `ffmpeg` process does the heavy lifting, all on the graphics card:
`ddagrab` (Windows Desktop Duplication) grabs the monitor as D3D11 textures, `h264_nvenc` encodes
them on the RTX's hardware encoder, and the result streams out as MPEG-TS into Rewind's memory ring
(`ChunkRing`). MPEG-TS can be cut at any byte and still play, so saving a clip is "find the first
keyframe in the last N seconds (`TsCut` — ffmpeg marks them in the stream), then pour the bytes from
there straight into an ffmpeg that wraps them in an MP4" — no re-encoding, no temp file, no copy of
the buffer, about a second.

The other features ride on the same ring. A **long recording** appends every chunk that reaches the
ring to a `.ts` file on disk (so the length is limited by the disk, not memory) and wraps it into an
MP4 when you stop; if Rewind ever dies mid-recording the `.ts` is still playable and gets finished on
the next start. A **screenshot** sends the bytes from the newest keyframe through ffmpeg, which hands
back every decoded frame as a BMP; the last one is the screen right now. **Copy for Discord** works out
the bitrate that fits the length under the limit (`SharePlan`), drops to 720p/30 fps when that bitrate
would look bad, and mixes the audio tracks into one. The **chime** is synthesised in code and played
through Rewind's own audio session, so it is heard whatever the Windows sound scheme says. **"Clip that"**
has two engines (`voice_engine`): `windows` uses the offline speech recogniser built into Windows
(`System.Speech`) with a one-phrase grammar, so nothing leaves the PC; `riovoice` streams the mic to a
RioVoice speech-to-text server (`voice_url`, a `/ws/transcribe` WebSocket taking 16 kHz int16 PCM) and is
a lot more accurate. With RioVoice the mic is only streamed while someone is actually talking: a loudness
gate opens the stream, 400 ms of pre-roll goes first, and 900 ms of quiet closes it, so the server sits
idle the rest of the time.

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
appears next to the exe. To start with Windows: tick *Start with Windows* on the Settings tab.

**It keeps itself up to date.** Right after it starts, and then every 5 minutes, Rewind asks GitHub for the
latest release (12 asks an hour, inside the 60 GitHub allows without a login). When there is a
newer one it downloads `Rewind.exe` next to the running one, checks it against the release's
`Rewind.exe.sha256` (and that it really is the promised version), and waits until nothing is going on — no
long recording, no clip or export saving, and no game in front for a minute (in the first 3 minutes after a
start a game doesn't count: the replay buffer is still empty). Then it renames itself to
`Rewind.exe.old`, puts the new one in its place and restarts into it; a card says *Updated to vX.Y.Z*. If
the new one doesn't come up, the old one is put back and that version is never tried again
(`update-skipped.txt`). Offline or rate-limited just means a line in the log. `auto_update=off` (or the
Settings tab) turns it off; a copy with a `.git` folder next to it (a build from source) never updates.

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
4. To have it start with Windows: tick *Start with Windows* on the Settings tab (or put a shortcut to
   `Rewind.exe` in the folder that `shell:startup` opens).

Settings are in `config.txt` (created next to the exe on first run from `config.example.txt`) or in the
window's Settings tab. Non-NVIDIA cards: change `codec` handling in `src/FfmpegArgs.cs` to `h264_amf`
(AMD) or `h264_qsv` (Intel) — untested.

## Build
```
powershell -File build.ps1
```
Uses the C# compiler that ships inside Windows (`csc.exe` in `Microsoft.NET\Framework64\v4.0.30319`),
no Visual Studio needed. Builds `Rewind.exe`, then `rewind-tests.exe`, and runs the tests.
The version is stamped from the git tag (`git describe --tags`; the pushed tag in CI) into
`obj\Version.cs`; `build.ps1 -Version 2.2.0` forces one. Pushing a `v*` tag makes CI publish a release with
`Rewind.exe` and `Rewind.exe.sha256`, which is what every running copy updates to.
Needs `ffmpeg.exe` on PATH (a recent build with `ddagrab` and `h264_nvenc`; the yt-dlp winget one works).

## Files
| File | Job |
|---|---|
| `src/Program.cs` | entry point, `--save` / `--save-short` / `--record` / `--screenshot` / `--clips` / `--quit` / `--list`, single-instance guard |
| `src/TrayApp.cs` | tray icon and menu, hotkeys, the card/balloon/chime, lock/unlock, the game watch, the audio-device retry, the voice trigger, the long recording, the storage cap, the window |
| `src/CaptureSession.cs` | the pipeline as one thing: ring + taps + recorder + the three pause reasons; clips, screenshots and recordings start here |
| `src/Recorder.cs` | keeps the ffmpeg capture alive, feeds the ring, restarts on failure |
| `src/AudioTap.cs` | WASAPI loopback / mic capture, paced named-pipe writer |
| `src/ChunkRing.cs` | the in-memory replay buffer (a recording follows its `Added` event) |
| `src/TsCut.cs` | finds the first (clip) or newest (screenshot, recording) keyframe and the PAT/PMT tables before it |
| `src/ClipSaver.cs` | ring -> ffmpeg stdin -> MP4 remux; names the clip after the game in front |
| `src/SessionRecording.cs` | the long recording: ring -> .ts on disk -> MP4 on stop; finishes leftovers after a crash |
| `src/Screenshot.cs` | ring -> ffmpeg -> last decoded frame -> PNG |
| `src/SharePlan.cs` / `ShareExport.cs` | the bitrate/size maths for Discord and the encode that applies it |
| `src/StoragePolicy.cs` | which clips go when the folder passes the storage cap |
| `src/Sounds.cs` | the chimes, made in code, with a custom-wav override |
| `src/Toast.cs` | the on-screen "Clip saved" card and the REC pill |
| `src/VoiceClip.cs` / `RioVoice.cs` / `SpeechGate.cs` | the "clip that" listeners: Windows' speech recogniser, or a RioVoice server (mic gate + WebSocket); the resampler, loudness gate and phrase check are pure and tested |
| `src/Startup.cs` | the Start-with-Windows shortcut |
| `src/UpdatePolicy.cs` / `Updater.cs` / `Exports.cs` | the auto-updater: version compare, hash check, dev-build and idle decisions (pure, tested); the GitHub check, download, rename-swap, restart and roll back; the count of running exports |
| `src/AssemblyInfo.cs` | the exe's name and description (the version comes from `build.ps1`) |
| `src/FfmpegArgs.cs` / `FfmpegRun.cs` | builds the ffmpeg command lines: capture, remux, trim, share, GIF, screenshot (pure, tested); runs the file-to-file ones |
| `src/Config.cs` | `config.txt` parsing, validation, and writing it back with comments |
| `src/GameDetector.cs` / `ForegroundApp.cs` | what's in front and whether it counts as a game (pure, tested) |
| `src/ClipsForm.cs` / `ClipGrid.cs` / `SettingsTab.cs` / `TrimForm.cs` | the window: the form, the owner-painted grid (tiles fill the width), settings editor, trim/GIF dialog |
| `src/ClipLibrary.cs` / `ClipProbe.cs` | listing clips + screenshots and reading names; thumbnails, durations, preview frames and media info via ffmpeg |
| `src/HotkeySpec.cs` / `HotkeyWindow.cs` | hotkey parsing and the global RegisterHotKey window (four slots) |
| `src/Dxgi.cs` | finds which DXGI output is the primary monitor |
| `src/CoreAudio.cs` | the WASAPI COM interfaces |
| `src/RewindControl.cs` / `Shell.cs` / `KillOnCloseJob.cs` / `Log.cs` / `FfmpegLocator.cs` / `FfmpegFetcher.cs` | the window's seam to the tray app, explorer args, the ffmpeg kill-on-close job, the log, finding / fetching ffmpeg |
| `tests/Tests.cs` / `UpdateTests.cs` | unit tests (no framework needed) |
