# Development guide

How VoxShift is built, how its audio pipeline works, how it is tested and how a release is made. For the rules a
change has to follow, see [CONTRIBUTING.md](../CONTRIBUTING.md); for the signing setup, see
[code-signing.md](code-signing.md).

- [Requirements](#requirements)
- [Build, test and publish](#build-test-and-publish)
- [Project structure](#project-structure)
- [Audio architecture](#audio-architecture)
- [Application shell](#application-shell)
- [Testing](#testing)
- [Continuous integration](#continuous-integration)
- [Releasing](#releasing)

## Requirements

| | |
|---|---|
| SDK | .NET 10 SDK, feature band `10.0.1xx` (pinned in [`global.json`](../global.json)) |
| Building | Windows, Linux or macOS (`EnableWindowsTargeting` lets the Windows-only projects compile anywhere) |
| Running | Windows 11 x64, a microphone and a virtual audio cable such as [VB-CABLE](https://vb-audio.com/Cable/) |
| Scripts | PowerShell 7 (`pwsh`) on Windows for `scripts/*.ps1` |

## Build, test and publish

These are the same commands CI runs.

```bash
dotnet restore VoxShift.sln
```

```bash
dotnet format VoxShift.sln --verify-no-changes --no-restore
```

```bash
dotnet build VoxShift.sln -c Release --no-restore
```

```bash
dotnet test tests/VoxShift.Core.Tests -c Release --no-build
```

```bash
dotnet publish src/VoxShift/VoxShift.csproj -c Release -r win-x64 -o publish
```

`publish/VoxShift.exe` is a self-contained single-file executable of about 75 MB; it includes the .NET and WPF
runtimes, so users install nothing besides the virtual cable. Publishing also works on Linux and macOS, but the file
version resource is only written by Windows builds, and signing and the smoke test need Windows, so releases are
built there.

CI treats warnings as errors (`ContinuousIntegrationBuild`). To get the same behaviour locally:

```bash
dotnet build VoxShift.sln -c Release -p:TreatWarningsAsErrors=true
```

## Project structure

```
Directory.Build.props        product version (single source of truth) and shared build settings
global.json                  pinned SDK feature band
src/VoxShift.Core/           platform independent and unit tested: router, mix policy, soundboard mixer and library,
                             clip cache, resampler, limiter, WAV decoder, dock geometry, settings, logging, hotkeys
src/VoxShift.Audio/          Windows audio: WASAPI capture and playback (NAudio), process-loopback COM interop,
                             device discovery and hot-plug monitoring, the audio engine, the soundboard player
src/VoxShift/                WPF application: views, view models, design system (Themes/Theme.xaml), tray icon,
                             global hotkey, soundboard dock controller, smoke test and screenshot renderer
tests/VoxShift.Core.Tests/   xUnit tests for VoxShift.Core
scripts/                     package.ps1, smoke-test.ps1 and the signing scripts
.github/workflows/           ci.yml, release.yml, screenshots.yml
docs/                        this guide, code-signing.md, release notes, screenshots and their tooling
```

The rule of thumb: logic that does not need Windows goes into `VoxShift.Core` and gets tests; COM and Win32 interop
stays in `VoxShift.Audio` (audio) or `src/VoxShift/Services` (windows, tray, hotkey).

## Audio architecture

```
Microphone  (WASAPI shared-mode capture) ──► SourceChannel ──► mic gate ─┐
Application (process loopback capture) ───► SourceChannel ──► app gate ─┼─► limiter ─► CABLE Input ─► VRChat
Soundboard  (decoded clips) ─────────────► SoundboardMixer ─────────────┘   (AudioRouter)

Soundboard  (decoded clips) ─────────────► SoundboardMixer ─► limiter ─► your default playback device
                                                               (MonitorMix: soundboard only, never the microphone)
```

### Threads

| Thread | Does | Must not |
|---|---|---|
| Engine thread (one, MTA) | All device and COM work: opens and closes captures and outputs, reacts to device changes, runs the supervisor once per second | Be blocked by the UI |
| Capture threads | Convert the device format to stereo float and write it into a `SourceChannel` | Allocate per block, log per block, touch devices |
| Render threads (WASAPI) | Pull the mix from `AudioRouter` / `MonitorMix` | Do disk I/O, wait on the UI, allocate per block |
| UI thread | Commands, status display, meters (30 Hz timer), settings | Touch COM audio objects |

Every command of `AudioEngine` is queued to the engine thread, so UI actions, device notifications and recovery can
never race each other. The supervisor (`Ensure`) compares what should be running with what is, and restarts
whatever stopped: an unplugged microphone, a closed application, a removed or reconfigured output. Retries are
rate-limited; a device-change notification retries at once.

### The pieces

- **`SourceChannel`**: one input of the router. A jitter buffer (40 ms target, 200 ms hard limit) plus a
  drift-compensating windowed-sinc resampler (±0.2 %), so two devices on independent clocks neither run dry nor
  accumulate latency. A source that is not transmitted is trimmed continuously, so nothing stale can be sent later.
- **`MixPolicy`**: the one rule set that decides which sources VRChat hears, from four inputs: mode, *Talk over
  music*, *Auto-mute*, and whether soundboard voices are playing **right now** (taken from the mixer, never from
  remembered events, so a sound that fails or is dropped cannot leave the microphone muted).
- **`AudioRouter`**: applies the policy through one transmit gate per source. Gates fade (8 ms; 30 ms when the
  microphone returns after a sound, after a 100 ms hold) and obey *close before open*, so with *Talk over music* off
  the microphone and the application are never mixed, not even during a switch. The soundboard layer is added on
  top, then one peak limiter (−1 dBFS ceiling, instant attack, 150 ms release) protects the output.
- **`SoundboardMixer`**: plays pre-decoded clips with short fades. At most 32 voices; the oldest is faded out when
  that is exceeded, and the list has a hard bound of 64.
- **`SoundboardPlayer` / `ClipCache`**: decode files on thread-pool threads (own WAV reader, Windows Media
  Foundation for everything else), resample them to the device rate and cache the result with a 256 MB budget.
  Sounds are cut at five minutes. The render thread never touches the disk.
- **`ProcessLoopbackCapture`**: hand-written COM interop for `ActivateAudioInterfaceAsync` with
  `VAD\Process_Loopback`, which NAudio does not expose. It captures a whole process tree; a watchdog re-attaches the
  capture when it is silent while a process that did not exist at capture start is playing (browsers start their
  audio process on demand).

## Application shell

- `App.xaml.cs` is the composition root. `App.SmokeTest.cs` holds the `--smoke-test` mode.
- `MainWindow` owns the tray icon, the global hotkey, the docked `SoundboardDockWindow` and the optional
  `VrControllerWindow`. All windows bind to the same `MainViewModel`, so switching interface never touches audio.
- `LifecycleCoordinator` (Core, tested) serialises close, minimize-to-tray and shutdown, so repeated clicks, the tray
  menu and queued callbacks cannot run those workflows twice or re-enter them.
- Settings are one JSON file written atomically (temporary file, then replace); a corrupt file is backed up as
  `settings.json.corrupt` and replaced by defaults.

Command-line switches:

| Switch | Purpose |
|---|---|
| `--minimized` | Start hidden in the tray (needs *Minimize to tray*) |
| `--smoke-test <report file>` | Run the built-in self-test and exit (CI, `scripts/smoke-test.ps1`) |
| `--render-screenshots <directory>` | Render the documentation screenshots with sample data and exit |

`VOXSHIFT_SETTINGS_DIR` overrides the settings directory (used by the smoke test so it never touches real settings).

## Testing

### Unit tests

`tests/VoxShift.Core.Tests` covers the platform-independent logic: the behaviour matrix of the mix policy and the
router (including click-free fades and the auto-mute timing), drift compensation, the limiter, the soundboard mixer,
the clip cache, the WAV decoder, settings migration, the soundboard library, dock geometry, the process tree and the
close/shutdown lifecycle. `ReliabilityTests` runs capture, render and control threads against one router at the
same time and replays device outages and rate changes.

### Smoke test (Windows)

```powershell
dotnet publish src/VoxShift/VoxShift.csproj -c Release -r win-x64 -o publish
./scripts/smoke-test.ps1 -ExePath publish/VoxShift.exe -ExpectedVersion 0.1.0
```

It starts the real executable, which loads every window, starts the audio engine, enumerates devices and
applications, captures its own process through process loopback, imports and plays a soundboard file, checks the
mixing rules and drives the close / tray / VR Controller Mode workflows, then exits by itself. Hosted runners have no
microphone and no virtual cable, so device-dependent results are reported but not asserted.

### Manual checks before a release

Automated tests cannot open real devices. On a Windows 11 PC with a microphone, headphones and VB-CABLE, check:

1. Microphone mode and application mode are heard through *CABLE Output* (record it, or listen in VRChat).
2. Switching modes repeatedly, with and without *Talk over music*: no clicks, no stale audio after a switch.
3. Several sounds at once, stopping one, *Stop all*; the microphone is muted while they play and returns afterwards.
4. Unplug and replug the microphone; close and restart the captured application; disable and re-enable
   *CABLE Input* in the Windows sound settings. Each state is shown and recovers without restarting VoxShift.
5. Change the Windows default playback device while a sound plays (the headphone monitor follows).
6. Minimize to the tray and restore while audio plays; enter and leave VR Controller Mode during playback.
7. Quit during playback: the process ends and VRChat's input goes silent.

## Continuous integration

| Workflow | Trigger | What it does |
|---|---|---|
| `ci.yml` | pull requests, pushes to `main`, manual | Linux: key-material check, restore, format check, warning-free build, unit tests. Windows: build, tests, publish, smoke test, the signing tool chain with a throwaway certificate, packaging |
| `release.yml` | tag `v*`, manual with a tag | Builds, tests, signs, verifies, smoke-tests, packages and publishes a release (below) |
| `screenshots.yml` | manual | Renders the real windows with sample data into `docs/screenshots` and commits them to the branch it ran on |

NuGet's vulnerability audit runs during restore; because CI treats warnings as errors, a known-vulnerable package
fails the build. Dependabot proposes NuGet and GitHub Actions updates monthly.

## Releasing

The version lives in `Directory.Build.props` (`<VersionPrefix>`). A release `vX.Y.Z` needs:

1. `<VersionPrefix>X.Y.Z</VersionPrefix>`.
2. A `## [X.Y.Z]` section in `CHANGELOG.md`.
3. `docs/release-notes/vX.Y.Z.md`; its first heading becomes the release title.
4. Fresh screenshots if the UI changed: run the **Screenshots** workflow on the release branch and wait for its commit.
5. Everything merged to `main` with green CI.

Then either push the tag, or run the **Release** workflow manually on `main` and enter the tag (it creates the tag on
that commit together with the release):

```bash
git tag -a vX.Y.Z -m "VoxShift vX.Y.Z"
```

```bash
git push origin vX.Y.Z
```

The release workflow stops before publishing unless all of the following hold: the signing secret is present and
valid, the commit is part of `main`, the tag equals `v<VersionPrefix>`, the changelog entry and release notes exist,
every screenshot the README embeds is in the commit, the format check, build and tests pass, the executable is
signed and timestamped, the signature verifies against the expected key, the signed executable passes the smoke
test, and the packaged files match their checksums. It publishes:

```
VoxShift-vX.Y.Z-win-x64-portable.exe
VoxShift-vX.Y.Z-win-x64.zip
SHA256SUMS.txt
```

and appends a *Digital signature* section (signer, key fingerprint, trust status) to the release notes.
