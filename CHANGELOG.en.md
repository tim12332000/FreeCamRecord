# Changelog

[繁體中文](CHANGELOG.md) · English

## 0.6.0 — 2026-10-31

- Add **Take photo** (the camera icon) to save the selected region as a PNG in the configured recording output folder.
- Allow photos during recording, with save feedback and distinct filenames for consecutive photos.
- Preserve the exact selected photo dimensions, including odd widths and heights.
- Adjust toolbar layout and verify non-overlapping controls in both languages and at minimum window width.
- Add desktop integration tests for photos, consecutive saves, and photos during recording.

## 0.5.0 — 2026-10-26

Public release incorporating the earlier local v0.4.x trials.

- Add **Language / 語言 → English / 繁體中文**, with a saved language preference. Traditional Chinese remains the default.
- Translate buttons, tooltips, menus, region selection, errors, and Help into English.
- Show the running v0.5.0 version in Help and provide English usage and release documentation.
- Retain v0.4.2 recording behavior, region selection, focus handling, and physical-clock audio/video timing.

Verification for this release is offline. It does not establish interactive English UI coverage or fixes for every game's recording stalls.

## 0.4.2 — Local trial, not separately published

- Add Help / Instructions and About, showing the running version and region/recording instructions.
- Disable the Help dialog during recording to avoid taking focus from the source.

## 0.4.1 — Local trial, not separately published

- Use physical-clock time slots informed by ShareX/OBS scheduling concepts. Skip expired slots after delays; successful frame count no longer determines timestamps.
- Give MP4 explicit timestamps and short frame durations to avoid catch-up bursts or large numbers of encoder-generated duplicates.
- Write audio independently using QPC packet timestamps and a shared start time. Fill overflow or missing data with silence without shifting later audio.
- Fix the media end time when stopping; use AVI null frames for missing intervals.
- Add offline stall simulation and decoded-media verification without operating the user's games or browser.
- Preserve the v0.4.0 region-selection mode.

## 0.4.0 — Local trial, not separately published

- Add hover-based region suggestions with crosshairs, magnifier, size label, and confirm/cancel controls.
- Suggest rectangles from screenshot pixel lines and color boundaries rather than substituting window or web DOM bounds.
- Support switching candidates, pinning with a click, dragging a manual region, and adjusting edges. Cancel preserves the original region.
- Use explicit pixel coordinates for screenshot analysis and drawing to avoid implicit DPI scaling.
- Validate offline with saved and synthetic images; no further operation of the user's games or browser.

## 0.3.1 — 2026-10-26

First public release.

- Provide a transparent region frame with dragging, edge resizing, and exact pixel dimensions.
- Record and save automatically on stop, with system audio and an optional microphone.
- Support MP4 (H.264/AAC), AVI (MJPEG/PCM), and 15/30/60/120 FPS or Auto based on the recording monitor's current refresh rate.
- Prefer GPU desktop capture with a compatibility fallback.
- Prevent automatic vertical expansion when dragging to a screen edge.
- Fix toolbar focus stealing so sources that pause when unfocused can continue updating.
- Include an original app icon and consistent toolbar/menu icons.

Historical verification used the final Windows x64 executable, actual frame dragging and audio/menu/record/stop controls, and Chrome recording/playback at 30/60/120/Auto FPS with audio. This did not cover every game or multi-monitor setup.

Limits: high-FPS performance depends on hardware and region size; MP4 may pad odd dimensions by one black pixel; AVI stops and saves at approximately 1.8 GB per file.
