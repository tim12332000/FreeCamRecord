# FreeCamRecord

[繁體中文](README.md) · English

A small Windows region recorder: **position the frame → record → stop → save automatically**.

## Use

Download the Windows x64 ZIP from [GitHub Releases](https://github.com/tim12332000/FreeCamRecord/releases/latest), extract it, and open `FreeCamRecord.exe`. The self-contained build includes its runtime; no separate .NET or FFmpeg installation is required. Development publishes are written to `dist/FreeCamRecord.exe`.

- Drag empty space on the toolbar to move the recording frame. Resize its edges or corners, or click the size label to enter an exact width and height in pixels. Moving against a screen edge preserves the manual size instead of triggering Windows snapping.
- Use **Select recording area** (the frame icon, also available in the menu). Hover to see rectangular candidates based on visible pixel boundaries. Click to pin a candidate, use the wheel to switch candidates, and confirm the region. You can also drag a rectangle and adjust its edges. Esc cancels and keeps the original region.
- The frame is transparent and lets you operate the application underneath. There is no video preview.
- Click **REC**, then **Stop** to save automatically. The default destination is `Videos\FreeCamRecord` in your user profile. Click **Saved** or use the menu to open the folder.
- Click **Take photo** (the camera icon) to save the selected area as a PNG in the same output folder. Photos can also be taken while recording.
- The menu controls format, target frame rate, save location, system audio, and microphone. Defaults are MP4, Auto FPS, system audio on, and microphone off.
- In v0.5.0, choose **Language / 語言 → English / 繁體中文**. The choice is saved for future launches; Traditional Chinese remains the default. Buttons, tooltips, selection controls, errors, and Help follow the selected language.
- **Help / About** shows the running version and usage instructions. It is disabled during recording to preserve the source application's focus. Windows-provided dialogs and native error details may follow the Windows display language.
- Closing the app while recording stops and saves before exiting. The floating toolbar avoids taking focus, and starting or stopping returns focus to the application inside the region, helping sources that pause when unfocused.

## Formats and frame rates

- **MP4:** H.264 video and AAC audio; the default, with smaller files suitable for playback and sharing.
- **AVI:** MJPEG video and PCM audio; larger files. Recording stops and saves automatically at approximately 1.8 GB per file.
- Target frame rates: **Auto, 15, 30, 60, or 120 FPS**.
- **Auto** reads the recording monitor's current refresh rate at each start: for example, 144 Hz targets 144 FPS. If the region spans monitors, the monitor with the greatest overlap is used. A failed refresh-rate lookup falls back to 60 FPS. Windows display settings are unchanged.
- FPS is a target, not a guarantee or hardware VSync. Actual capture speed depends on region size, GPU, encoder, and system load.
- The physical-clock timing introduced in the local v0.4.1 trial is retained in v0.5.0. MP4 skips expired monotonic-clock time slots and preserves the gaps with explicit timestamps instead of catching up by recording many current frames. AVI uses null frames to hold the previous image on its fixed-rate timeline. Duration is not calculated from the number of successfully written images.
- MP4 pads an odd width or height with at most one black pixel on the right or bottom to satisfy H.264's even dimensions; the source is not stretched. AVI keeps the original dimensions.

## Limitations

- Region suggestions use local pixel geometry heuristics, not AI semantic recognition. Weak boundaries or similar colors may produce missing or incorrect candidates. Switch candidates or select manually. Selection uses a temporary desktop screenshot, uploads nothing, and does not change the recording source.
- The region stays fixed during recording; stop before moving or resizing it. The minimum size is 312 × 120 pixels to leave room for toolbar controls.
- Audio uses the Windows default playback and recording devices. Device failure stops recording with a message; disable the affected audio source to record without it.
- Protected video, the lock screen, and the Windows secure desktop are unsupported.
- There is no editor, player, live streaming, account, or cloud upload.
- Focus handling and timing changes do not establish compatibility with every game. Mixed-DPI monitors, microphone voice quality, and long recordings need validation in their respective environments.

## Build

Use the .NET 6 SDK with Windows Desktop support on Windows:

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

There are no third-party NuGet package dependencies or required external runtime tools. Capture prefers DXGI Desktop Duplication to copy the region from the GPU, with a GDI compatibility path for unsupported devices, rotation, or regions spanning monitors. Both reuse pixel buffers. Audio uses WASAPI, MP4 uses Windows Media Foundation, and AVI writes a RIFF container directly. MP4 permits system-selected hardware encoding and attempts software encoding if configuration fails; GPU encoding is not guaranteed.

The application icon and toolbar/menu line icons are original, locally drawn assets, without an external icon library or font. Rebuild the application icon with:

```powershell
powershell -NoProfile -File .\tools\Build-Icons.ps1
```

## Offline timing verification

The v0.5.0 language change preserves the existing recording and physical-clock timing behavior. Timing checks simulate stalls offline, without operating a desktop, browser, game, or physical audio device. They do not prove that all real-world game stalls are fixed, or that the English UI has been tested interactively.

```powershell
$test = Start-Process .\dist\FreeCamRecord.exe -ArgumentList '--timing-test' -PassThru -Wait -WindowStyle Hidden
$test.ExitCode
```

The test writes solid-color videos and synthetic audio to `.omx/verification/timing-<timestamp>/` under the working directory. Verify the decoded output separately with:

```powershell
python .\tools\verify-timing.py <output-directory> <ffmpeg-path>
```

The verifier checks decoded timestamps, holding the previous image through gaps, total duration, and audio position. Python NumPy and FFmpeg are developer verification tools only.

Timing behavior:

- Target time slots and successful writes are counted separately; after a delay, capture resumes at the current slot.
- Audio advances independently in 10 ms blocks on the same QPC timeline. WASAPI packets retain capture timestamps. Missing or expired data becomes silence; newer audio is not shifted earlier.
- Stop fixes the media end time immediately. Encoder finalization and waiting for the last audio packets do not extend the recording.
- MP4 uses short input-frame durations to avoid automatic duplicate-frame generation. A long final gap may add at most one final hold image.
- Timing diagnostics are saved under `%LOCALAPPDATA%\FreeCamRecord\logs\` as `.timing.json` files, including write counts, skipped slots, and approximate audio timestamp counters.

The scheduling concepts were informed by [ShareX's recording loop](https://github.com/ShareX/ShareX/blob/master/ShareX.ScreenRecordingLib/ScreenRecorder.cs) and [OBS video scheduling](https://github.com/obsproject/obs-studio/blob/master/libobs/obs-video.c), with an independent implementation and no added third-party runtime or copied GPL source.

## Optional interactive developer checks

These are separate from offline release checks. `--self-test` requires a signed-in interactive Windows desktop and briefly moves the mouse, opens test windows, and plays test audio. It exercises region movement/resizing, click-through, saving, audio startup, odd dimensions, repeated recordings, close-to-save, error paths, menus, formats, and target frame rates. `--media-test` runs the format/frame-rate checks without dragging the mouse. Outputs go to `.omx/verification/<timestamp>/` in the working directory.

```powershell
$test = Start-Process .\dist\FreeCamRecord.exe -ArgumentList '--self-test' -PassThru -Wait
$test.ExitCode
```

`tools/motion-test.html` provides moving blocks, scrolling text, and a visible binary source-frame counter updated by `requestAnimationFrame`. `tools/browser-motion-test.mjs` opens it in an isolated Chrome profile and records the actual desktop browser window with the recording engine. `tools/analyze-motion.py` reads frame counters from decoded video to measure distinct source frames per second, duplicates, and stalls.

```powershell
node .\tools\browser-motion-test.mjs .\dist\FreeCamRecord.exe browser dxgi
```

These checks require local Chrome, Node, FFmpeg, and Python Pillow; none are app runtime dependencies. Historical v0.3.1 interactive checks used Windows 10 x64 with one monitor at 150% scaling and exercised Chrome at 30/60/120/Auto FPS. Those results apply to that environment, not every game or monitor setup. Browser playback callbacks may coalesce and cannot alone prove perfect 120 FPS. Audio presence and alignment require decoded audio/video analysis.
