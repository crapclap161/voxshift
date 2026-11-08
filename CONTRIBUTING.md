# Contributing to VoxShift

Thanks for helping improve VoxShift! This guide explains how to set up the project, the quality bar for changes,
and how pull requests are reviewed.

## Getting started

1. Install the **.NET 10 SDK** (the exact feature band is pinned in `global.json`).
2. Fork and clone the repository, then restore the dependencies:

   ```bash
   dotnet restore VoxShift.sln
   ```

3. Build and test:

   ```bash
   dotnet build VoxShift.sln -c Release
   ```

   ```bash
   dotnet test tests/VoxShift.Core.Tests
   ```

The solution builds on Windows, Linux and macOS (`EnableWindowsTargeting`), but **running** the app and anything
that touches audio devices requires Windows 11 (or Windows 10 build 20348+) with a virtual audio cable such as
VB-CABLE.

## Project layout

| Project | Responsibility | Platform |
|---|---|---|
| `src/VoxShift.Core` | DSP (router, resampler, limiter), settings, logging, process grouping, hotkey parsing | any (unit tested) |
| `src/VoxShift.Audio` | WASAPI capture/render, process-loopback COM interop, device monitoring, `AudioEngine` | Windows |
| `src/VoxShift` | WPF UI, view model, tray icon, global hotkey | Windows |
| `tests/VoxShift.Core.Tests` | xUnit tests for `VoxShift.Core` | any |

Put platform-independent logic in `VoxShift.Core` and cover it with tests. Keep COM and Win32 interop inside
`VoxShift.Audio` (or `src/VoxShift/Services` for UI-only interop) and document non-obvious interop.

## Quality expectations

Every change must pass the same checks CI runs:

```bash
dotnet format VoxShift.sln --verify-no-changes
```

```bash
dotnet build VoxShift.sln -c Release -p:TreatWarningsAsErrors=true
```

```bash
dotnet test tests/VoxShift.Core.Tests
```

- **No warnings.** Code analysis runs at `latest-recommended`, and CI treats warnings as errors. Suppress a rule only
  with a written justification (`SuppressMessage(..., Justification = "...")`).
- **Formatting** follows `.editorconfig`. Run `dotnet format VoxShift.sln` before committing.
- **Audio thread safety.** Never allocate, block or log on the render/capture hot path. Device/COM work belongs on
  the engine thread.
- **Never mix sources.** The router guarantees exactly one transmitted source. Changes in `AudioRouter`/`SourceChannel`
  need tests proving this still holds.
- **No telemetry, no network calls,** and no new dependencies without a clear need (explain them in the PR).
- **User-facing text** should be short, specific and actionable (say what happened and what to do).
- If you change the UI, run the *Screenshots* workflow (or `VoxShift.exe --render-screenshots docs/screenshots`
  followed by `python3 docs/tools/compose_hero.py`) so the README stays accurate.

On Windows, the smoke test runs the real executable:

```powershell
dotnet publish src/VoxShift/VoxShift.csproj -c Release -r win-x64 -o publish
./scripts/smoke-test.ps1 -ExePath publish/VoxShift.exe -ExpectedVersion 0.0.3
```

## Pull requests

1. Create a branch from `main` (`feature/…`, `fix/…`, `docs/…`).
2. Keep pull requests focused: one logical change, with tests where possible.
3. Use [Conventional Commits](https://www.conventionalcommits.org/) for commit messages, e.g.
   `fix(audio): recover when the cable is re-enabled`.
4. Add an entry under **[Unreleased]** in `CHANGELOG.md` for user-visible changes.
5. Describe *what* changed and *why*, and how you tested it (especially manual audio tests on real hardware).
6. CI must be green before review. A maintainer merges with squash-merge.

## Releasing (maintainers)

1. Move the **[Unreleased]** entries in `CHANGELOG.md` to a new version section.
2. Set `<VersionPrefix>` in `Directory.Build.props` to the new version.
3. Add `docs/release-notes/vX.Y.Z.md`.
4. Merge to `main`, then tag and push:

   ```bash
   git tag -a vX.Y.Z -m "VoxShift vX.Y.Z"
   ```

   ```bash
   git push origin vX.Y.Z
   ```

Alternatively, start the **Release** workflow manually in the Actions tab and enter the tag (e.g. `vX.Y.Z`).
It creates the tag on the selected commit together with the release.

The release workflow refuses to publish if the tag, `VersionPrefix`, changelog entry or release notes don't match.

## Reporting bugs and security issues

Open an issue using the bug template and attach the relevant part of `%LOCALAPPDATA%\VoxShift\logs\VoxShift.log`.
For security issues, follow [SECURITY.md](SECURITY.md) instead of opening a public issue.
