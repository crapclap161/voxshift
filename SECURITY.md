# Security Policy

## Supported versions

Only the latest release of VoxShift receives fixes.

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Report them privately via
[GitHub Security Advisories](https://github.com/crapclap161/voxshift/security/advisories/new) and include the
VoxShift version, Windows build, steps to reproduce, and the expected and actual behaviour.

## Scope notes

VoxShift runs as a normal user, needs no administrator rights, makes no network connections, and only writes to
`%APPDATA%\VoxShift` (settings) and `%LOCALAPPDATA%\VoxShift\logs` (log). Release binaries are built by GitHub
Actions from tagged sources and published with SHA-256 checksums (`SHA256SUMS.txt`). From v0.0.8 on, releases are
Authenticode-signed with a self-signed project certificate (integrity and key identity only; not publicly trusted, see
[docs/code-signing.md](docs/code-signing.md)); v0.0.7 and older are unsigned.
