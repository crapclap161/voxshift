# Changelog

All notable changes to VoxShift are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/crapclap161/voxshift/compare/v0.0.2...HEAD
[0.0.2]: https://github.com/crapclap161/voxshift/compare/v0.0.1...v0.0.2
[0.0.1]: https://github.com/crapclap161/voxshift/releases/tag/v0.0.1
