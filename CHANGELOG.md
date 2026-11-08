# Changelog

All notable changes to VoxShift are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.0.5] - 2026-11-08

**VR Control & Smart Audio Update.** A redesigned, VR-friendly interface, a soundboard that docks to the window,
optional voice-over-music mixing and automatic microphone muting while sounds play.

### Added
- **Auto-mute microphone during Soundboard playback** (on by default). Starting a sound mutes only the microphone's
  contribution inside VoxShift's mix (8 ms fade); it returns automatically (30 ms fade) once the *last* sound has
  really finished, as reported by the mixer, not by a timer. Overlapping sounds, Stop all, stopping single sounds,
  failed playback and device loss are all handled, a 100 ms hold bridges quick re-triggers, and the decision is
  recalculated from the current settings every 10 ms, so a stale state can never be restored. The Windows microphone
  and other applications are never touched, and headphone monitoring keeps playing.
- **Talk over music** (off by default). In application mode VRChat hears the application *and* your microphone,
  each with its own volume, through the existing −1 dBFS limiter. Off keeps the classic switch with no microphone
  leakage. Auto-mute takes precedence while sounds play: only the voice pauses, the music continues.
- **Dockable soundboard.** The soundboard is now a separate window attached to the right edge of the main window. It
  follows moves and resizes (physical-pixel placement against the visible frame, correct at any DPI and on any
  monitor), nudges the main window left when there is no room, never opens off-screen, minimizes/restores/hides with
  the main window, follows *Always on top*, can be resized, and collapses into a slim strip with a large expand
  handle. Drag & drop works on the dock and on the collapsed strip. Closing it only hides it.
- **VR Controller Mode.** An optional simplified window with very large controls for VR desktop overlays: status tiles
  (transmitting, microphone live/muted, talk over music, auto-mute, VRChat output, sounds playing), huge
  MICROPHONE / APPLICATION AUDIO buttons, TALK OVER MUSIC and AUTO-MUTE MIC ON SOUNDS switches, collection chips, a
  scrollable grid of large sound buttons with page buttons, STOP ALL and BACK TO FULL INTERFACE. It shares the one
  view model and audio engine with the full interface, so switching never interrupts or duplicates audio. The tray
  icon always offers *Show full interface*, and the window's X returns to it as well.
- **Edit mode** on the soundboard: rename, relink and remove sounds without right-clicking.
- Tray menu entries for Talk over music, Auto-mute, VR Controller Mode and *Show full interface*.
- Shortcuts: Ctrl+T (talk over music), Ctrl+B (show/hide soundboard), Esc (leave VR Controller Mode).
- Settings schema 4: `AutoMuteMicrophoneDuringSoundboard`, `TalkOverMusicEnabled`, `VrControllerModeEnabled`,
  `SoundboardDockVisible`, `SoundboardDockExpanded`, `SoundboardDockWidth`, VR window geometry and the main window
  height. v0.0.4 (and older) settings migrate automatically; soundboard collections, file references, devices,
  volumes, hotkeys, monitoring and window settings are kept.
- 52 new unit tests: the complete behaviour matrix, auto-mute timing, overlapping sounds, Stop all, single stops,
  interrupted playback, quick re-triggers, click-free fades, settings changes during playback, mode switches during
  playback, leakage checks in both directions, independent volumes, limiter and meter with all three sources, device
  disconnects, headphone monitoring isolation, settings migration and dock placement (DPI, multi-monitor, narrow
  screens). The Windows smoke test now also checks smart mixing, the dock and VR Controller Mode.

### Changed
- **Redesigned interface.** Status card with large chips that show the *effective* state, bigger mode buttons,
  an *Audio mixing* card, large ON/OFF option switches, large sliders (click anywhere to jump), 48 px combo boxes and
  buttons, and clear hover/pressed/focus/disabled states throughout. The soundboard no longer takes space inside the
  main window.
- `AudioRouter` now uses one transmit gate per source driven by the new `MixPolicy`. With Talk over music off the
  gates still close before they open, so microphone and application are never mixed (unchanged guarantee).
- The screenshots workflow runs only on demand (and never on tags), and the release workflow verifies that every
  screenshot the README embeds is part of the tagged commit. This prevents screenshot commits landing after a tag.
- README rewritten for v0.0.5 with new screenshots of the real UI.

### Removed
- The embedded soundbar in the main window (replaced by the dock) and its old screenshots.

## [0.0.4] - 2026-11-08

### Fixed
- **You can now hear your own soundboard sounds.** Before, sounds were only sent to VRChat (the virtual microphone),
  so you didn't hear what you played.

### Added
- **Hear sounds myself** option in the soundboard (on by default) with its own volume slider (0–200 %). Each sound
  also plays on your Windows default playback device (your headphones), independent of the volume VRChat hears.
- The monitor follows Windows default-device changes and recovers after device errors. It never plays into the
  virtual cable: if the default playback device is the cable, monitoring pauses with a hint, so VRChat never hears
  sounds twice.
- Only the soundboard is monitored. Your microphone is not echoed back to you.
- If the virtual microphone isn't running, sounds still play in your headphones, with a hint that VRChat can't hear
  them.
- Settings schema 3 (`SoundboardMonitorEnabled`, `SoundboardMonitorVolumePercent`). Settings from 0.0.1 to 0.0.3
  load unchanged, with monitoring enabled.
- 12 new unit tests (monitor mix, independence of the VRChat and headphone mixes, no mic/app audio in the monitor,
  limiter, never monitoring into the cable, settings migration). The smoke test reports the monitor state.

### Changed
- The WASAPI output is generalized (`StereoRenderOutput`) and drives both the virtual cable and the headphone
  monitor. Soundboard voices for a device are dropped when it stops, so no stale sounds play later.

## [0.0.3] - 2026-11-08

**Soundboard Feature Release.** VoxShift can now play local sound files into VRChat, on top of your microphone or
application audio.

### Added
- **Local soundboard.** Click a button to play a sound through the virtual microphone, so VRChat hears it. Several
  sounds can play at once, and clicking a playing sound stops it. **Stop all** stops everything.
- **Mixed with the active source.** Microphone + soundboard, or application audio + soundboard. The soundboard is
  an extra layer. The rule that microphone and application audio are never transmitted together is unchanged, and
  sounds keep playing when you switch modes.
- **Drag & drop from Windows Explorer.** Drop one or more audio files, or a folder, onto the soundbar. It is
  highlighted as a drop target, also when collapsed. If you have no soundboard yet, one is created automatically.
  Unsupported, missing and duplicate files are skipped with a clear message, and the soundbar expands after a
  successful import.
- **Add sounds** button with the Windows file picker.
- **Multiple soundboard collections** that you can create, rename, delete and switch between.
- **Sound buttons** with a playing state and progress bar. The right-click menu offers play (overlap), stop,
  rename, relink file, show in Explorer and remove.
- **Missing-file handling.** Moved or deleted files are flagged with a warning, can be relinked, and never break
  the soundboard.
- **Independent soundboard volume** (0–200 %), smoothed and protected by the existing −1 dBFS limiter. The
  *To VRChat* meter includes soundboard audio.
- **Collapsible soundbar** with a short animation. The collapsed or expanded state is remembered.
- **Supported formats:** WAV (VoxShift's own decoder), plus MP3, FLAC, M4A/AAC and WMA through Windows Media
  Foundation.
- Soundboard collections, sounds, volume, expanded state and the selected collection are saved in `settings.json`.

### Local files
- VoxShift stores the **absolute path** of each sound and plays the original file in place. It never copies,
  moves, uploads or modifies imported files. Removing a sound or deleting a collection never deletes the audio file.

### Changed
- README: new Soundboard section with usage instructions and three new screenshots (expanded, collapsed,
  drag & drop). The hero image now shows the soundboard.
- Settings schema version 2. Settings from 0.0.1/0.0.2 load unchanged, with soundboard defaults.
- The Windows smoke test now also imports, decodes, resamples and plays a real WAV file through the soundboard.
- Release titles are taken from the release notes heading.

### Tests
- 40 new unit tests: library (collections, imports, duplicates, unsupported and missing files, relinking, Unicode
  paths), settings round-trip and migration, mixer (simultaneous sounds, stop and stop all, volume, fades, voice
  limit, cleanup), routing (microphone or application plus soundboard, mode switch while playing, limiter, meter),
  WAV decoding, resampling, and an end-to-end file pipeline that verifies the original file is untouched.

## [0.0.2] - 2026-11-08

Maintenance release: build and release pipeline updates. The application's features and behaviour are unchanged.

### Changed
- GitHub Actions updated to their current major versions (`actions/checkout` v7, `actions/setup-dotnet` v6,
  `actions/upload-artifact` v7, `softprops/action-gh-release` v3). This removes the Node.js 20 deprecation warnings.
- CI runners pinned to explicit images (`ubuntu-24.04`, `windows-2025`) for reproducible builds. This also removes the
  `ubuntu-latest` migration notice.
- Test runner updated to `xunit.runner.visualstudio` 4.0.1 (all 65 tests unchanged and passing).
- CI preview artifacts contain the portable EXE and checksums only, which halves their size.
- Dependabot groups GitHub Actions updates into a single pull request.

## [0.0.1] - 2026-11-08

First public release of VoxShift (previously developed as “VRChat Audio Switcher”).

### Added
- Two transmission modes, **Microphone** and **Application audio**, switched with one click. Exactly one source is
  sent at a time; a switch fades the current source out completely (8 ms) before the next one fades in.
- Per-application capture through the Windows process-loopback API. Only the selected program and its child
  processes are captured, and the program keeps playing to your own headphones.
- Output into a virtual audio cable (VB-CABLE or similar). The cable is detected automatically, and VRChat records
  from it as its microphone.
- Independent microphone and application volume (0–200 %), smoothed gain and a −1 dBFS peak limiter.
- Live level meters for microphone, application and the final signal, plus an ON AIR indicator.
- Application list grouped per program (multi-process browsers count as one entry), with a playing-audio indicator.
- Automatic recovery from microphone or cable disconnects, Windows default-device changes and application restarts.
- Clock-drift compensation (adaptive resampling) for long sessions.
- Optional global toggle hotkey, in-window shortcuts (Ctrl+1, Ctrl+2, F5), always-on-top, tray mode and
  start-in-tray.
- Built-in setup guide, a VB-CABLE call-to-action when the driver is missing, and warnings for common
  feedback-loop misconfigurations.
- Persistent settings, with automatic one-time import of settings from VRChat Audio Switcher.
- Rotating local log file. No network access and no telemetry.

### Changed (compared to VRChat Audio Switcher 1.0.0)
- Rebranded to VoxShift: executable `VoxShift.exe`, settings in `%APPDATA%\VoxShift`, logs in
  `%LOCALAPPDATA%\VoxShift\logs`.
- Refined UI: brand bar with version, loading and empty states for the application list, screen-reader
  announcement of the transmission state, keyboard shortcuts.
- Hardened code base: code analysis at `latest-recommended` with zero warnings, consistent formatting
  (`dotnet format`), culture-invariant logging, safer Win32 interop buffers.
- Reproducible builds: deterministic compilation, exactly pinned dependencies and SDK feature band,
  normalised archive timestamps and SHA-256 checksums.
- New CI (Linux validation plus a Windows build and smoke test) and a tag-driven release workflow that publishes a
  portable single-file `VoxShift-vX.Y.Z-win-x64-portable.exe`, a ZIP with documentation, and `SHA256SUMS.txt`.

[Unreleased]: https://github.com/crapclap161/voxshift/compare/v0.0.5...HEAD
[0.0.5]: https://github.com/crapclap161/voxshift/compare/v0.0.4...v0.0.5
[0.0.4]: https://github.com/crapclap161/voxshift/compare/v0.0.3...v0.0.4
[0.0.3]: https://github.com/crapclap161/voxshift/compare/v0.0.2...v0.0.3
[0.0.2]: https://github.com/crapclap161/voxshift/compare/v0.0.1...v0.0.2
[0.0.1]: https://github.com/crapclap161/voxshift/releases/tag/v0.0.1
