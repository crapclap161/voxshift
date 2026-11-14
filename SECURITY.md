# Security Policy

## Supported versions

Only the latest release of VoxShift receives fixes.

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Report them privately via
[GitHub Security Advisories](https://github.com/crapclap161/voxshift/security/advisories/new) and include the
VoxShift version, Windows build, steps to reproduce, and the expected and actual behaviour.

Please give the maintainer reasonable time to publish a fix before disclosing details.

## What VoxShift does and does not do

- It runs as a normal user and needs no administrator rights. (The separate VB-CABLE driver does, once, to install.)
- It makes no network connections: no telemetry, no update check, no uploads.
- It writes only to `%APPDATA%\VoxShift` (settings) and `%LOCALAPPDATA%\VoxShift\logs` (log).
- Sound files are opened read-only from where they are; they are never copied, modified or deleted.
- It reads the list of running processes and their window titles to let you choose an application to capture, and
  it captures audio only from the application you select.

## Release integrity

- Release binaries are built by GitHub Actions from a commit on `main` and published with SHA-256 checksums
  (`SHA256SUMS.txt`).
- From v0.0.8 on, the executable is Authenticode-signed with a **self-signed** project certificate. This establishes
  integrity and the identity of the signing key only. It is **not** publicly trusted, it is not a verified publisher
  in the eyes of Windows, and it does not remove SmartScreen warnings. v0.0.7 and older are unsigned.
- How to verify a download and what the signature means: [README](README.md#windows-security-warnings-and-verifying-a-download)
  and [docs/code-signing.md](docs/code-signing.md).
- No signing key, certificate or passphrase is stored in the repository; CI fails if key material is committed.
