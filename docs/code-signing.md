# Code signing (maintainer guide)

VoxShift releases are signed with **Authenticode** using a certificate and private key that you control. The key lives in
two **GitHub Actions repository secrets**; no cloud signing service, Azure account or external identity verification is
involved.

> [!IMPORTANT]
> The default certificate is **self-signed**. Read [What a self-signed signature does and does not give you](#what-a-self-signed-signature-does-and-does-not-give-you)
> before telling users anything about it.

## Quick start (about 5 minutes, once)

1. **On a Windows PC**, open PowerShell in a clone of the repository (or just download the script) and run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\create-signing-certificate.ps1
   ```

   Choose a password of at least 16 characters when asked. The script writes three files to
   `Documents\VoxShift-signing\` (outside the repository) and prints the certificate **thumbprint**:

   | File | Contains | What to do |
   |---|---|---|
   | `VoxShift-codesign.pfx` | certificate **and private key**, password-protected | **Back it up** (see below). Never commit it. |
   | `VoxShift-codesign.cer` | public certificate only | Safe to share or commit. Lets users compare the thumbprint. |
   | `WINDOWS_CODESIGN_PFX_BASE64.txt` | the PFX as Base64 | Paste into the first secret, then **delete the file**. |

2. **Add two repository secrets** at *Settings → Secrets and variables → Actions → New repository secret*:

   | Secret name | Value |
   |---|---|
   | `WINDOWS_CODESIGN_PFX_BASE64` | the entire content of `WINDOWS_CODESIGN_PFX_BASE64.txt` |
   | `WINDOWS_CODESIGN_PFX_PASSWORD` | the password you chose |

   Prefer the command line? With the [GitHub CLI](https://cli.github.com/) (`gh auth login` once):

   ```powershell
   Get-Content -Raw "$HOME\Documents\VoxShift-signing\WINDOWS_CODESIGN_PFX_BASE64.txt" | gh secret set WINDOWS_CODESIGN_PFX_BASE64 --repo crapclap161/voxshift
   gh secret set WINDOWS_CODESIGN_PFX_PASSWORD --repo crapclap161/voxshift
   ```

   (The second command prompts for the value.) Already have a `.pfx`? Produce the Base64 value yourself:

   ```powershell
   [Convert]::ToBase64String([IO.File]::ReadAllBytes('C:\path\to\certificate.pfx')) | Set-Clipboard
   ```

3. **Optional pin:** add a repository *variable* (not a secret) `WINDOWS_CODESIGN_THUMBPRINT` with the thumbprint the
   script printed. The release then refuses to sign if a different certificate is ever stored in the secret by mistake.
4. **Release:** run the **Release** workflow (*Actions → Release → Run workflow*, tag `v0.0.8`) or push the tag. The
   first thing it does is validate the secrets, so a missing or wrong secret fails within seconds with instructions,
   before anything is built or published.

## How the pipeline works

`.github/workflows/release.yml`:

1. **Validate signing certificate.** `scripts/sign-executable.ps1 -ValidateOnly` decodes the secrets in memory and checks
   the password, private key, Code Signing key usage and validity period. Missing or invalid secrets stop the release.
2. Validate the tag against `Directory.Build.props`, `CHANGELOG.md`, release notes and README screenshots; restore,
   format check, build, unit tests; `dotnet publish` (self-contained single file).
3. **Sign** `publish/VoxShift.exe` with `signtool` (SHA-256) and add an **RFC 3161 timestamp** (DigiCert, with Sectigo
   and GlobalSign as fallbacks; no timestamp means no release). The PFX is decoded to a private temporary file, imported
   with a non-exportable key and deleted at once; the key is removed from the runner afterwards (also when the job fails).
   The secrets are visible only to the validate and sign steps and are masked by GitHub in logs. They are passed through
   environment variables, never on a command line, and are never printed.
4. **Verify** (`scripts/verify-signature.ps1`): the file is signed and unmodified, signed by the **expected certificate**
   (thumbprint taken from the secret), has a timestamp and a SHA-256 digest, and `signtool verify /pa` passes.
5. Smoke test of the **signed** executable, then packaging (portable EXE, ZIP, `SHA256SUMS.txt`), **after** signing.
6. Verify again the portable EXE **and** the EXE inside the ZIP, check that both are identical, and that
   `SHA256SUMS.txt` contains the checksums of the files being uploaded.
7. Create the GitHub Release. The release text gets a *Digital signature* section generated from the certificate itself
   (signer, thumbprint, self-signed or publicly trusted).

Ordinary CI (`ci.yml`, pull requests) never receives the secrets. It runs `scripts/test-signing.ps1`, which creates a
**throwaway** certificate, signs a copy of the build and proves that the gates work: the signed file verifies and runs,
while unsigned files, tampered files, a wrong expected certificate, a wrong password, missing secrets and
self-signed-as-public-trust are all rejected. (Timestamping needs a network service, so only the release exercises it.) A second
CI check fails if a key or PFX is ever committed.

## What a self-signed signature does and does not give you

Four different things are easy to confuse:

| | Self-signed (default) | Publicly trusted certificate |
|---|---|---|
| **Cryptographic integrity**: the file was not modified after signing | ✅ yes | ✅ yes |
| **Identity of the key holder**: signed with *this* key (compare the thumbprint) | ✅ yes, if the user knows the thumbprint | ✅ yes, and a CA vouched for the publisher name |
| **Public trust chain**: Windows trusts the certificate on its own | ❌ **no** | ✅ yes |
| **SmartScreen reputation** | ❌ **not improved** | ⚠️ not guaranteed either; builds up with downloads |

So with the self-signed key:

- Windows shows the publisher as unknown or untrusted and may still show *“Windows protected your PC”*.
- The signature is **not** a “verified publisher” and must not be described as such.
- `Get-AuthenticodeSignature` reports a status such as `UnknownError` or `NotTrusted` (untrusted root), *not* `Valid`.
  A status of `HashMismatch` means the file was changed after signing.
- It is still useful: it lets someone who knows the thumbprint confirm that a file really comes from the holder of the
  VoxShift release key and was not tampered with, and it keeps the same identity across releases.
- Publishing the public certificate in a GitHub repository does **not** make Windows trust it.

> Users should not be asked to install your certificate into their Trusted Root store. That would let anyone holding
> the key sign software that Windows then trusts on their machine.

### How to verify a downloaded file

1. **Checksum:** `Get-FileHash .\VoxShift-v0.0.8-win-x64-portable.exe -Algorithm SHA256` must equal the line in
   `SHA256SUMS.txt`.
2. **Signature and thumbprint:**

   ```powershell
   $s = Get-AuthenticodeSignature .\VoxShift-v0.0.8-win-x64-portable.exe
   $s.Status                      # NotTrusted / UnknownError for a self-signed key is expected; HashMismatch or NotSigned is NOT ok
   $s.SignerCertificate.Subject
   $s.SignerCertificate.Thumbprint   # must equal the thumbprint in the release notes
   $s.TimeStamperCertificate.Subject # present = timestamped
   ```

3. **Strict check (maintainers, administrator PowerShell):** this temporarily trusts only the pinned certificate to let
   Windows verify the signature cryptographically, then removes it again:

   ```powershell
   .\scripts\verify-signature.ps1 -Path .\VoxShift.exe -ExpectedThumbprint <thumbprint> -RequireTimestamp -TrustSelfSigned
   ```

   The output lists integrity, signer, thumbprint, timestamp and states plainly that a self-signed certificate is *not*
   publicly trusted.

## Backing up the key

The PFX is the **only** copy of the private key (the script removes it from your Windows certificate store).

- Keep **two** copies of `VoxShift-codesign.pfx` in different places (a password manager attachment and an offline USB
  stick or encrypted cloud vault), and store the **password separately** from the file.
- GitHub secrets cannot be read back, so the repository is *not* a backup.
- If the key is lost or leaked you cannot recover it: create a new certificate (rotation, below). If it leaked,
  delete the GitHub secrets immediately and say so in the next release notes.
- Never commit the PFX, the Base64 text or the password. `.gitignore` blocks the usual file names and CI fails if key
  files are committed.

## Rotating or renewing

The certificate is valid for 10 years by default; the release warns when fewer than 60 days remain, and refuses to sign
with an expired certificate. A new certificate is a **new identity** (different thumbprint), so rotate only when needed:

1. Run `create-signing-certificate.ps1 -OutputDirectory <new folder>` (or `-Force`; the script refuses to overwrite by
   default so the identity cannot change by accident).
2. Replace both secrets (and the optional `WINDOWS_CODESIGN_THUMBPRINT` variable).
3. Mention the new thumbprint in the next release notes. Old releases keep their valid signatures and timestamps.

Rotation after a leak: delete the secrets first, then follow the steps above.

## Upgrading to a publicly trusted certificate later

The pipeline needs no redesign. Buy or obtain an OV/EV code-signing certificate that can be exported as a PFX (note
that many CAs issue keys on hardware tokens or cloud HSMs which cannot be exported; check this before buying), then:

1. Replace `WINDOWS_CODESIGN_PFX_BASE64` and `WINDOWS_CODESIGN_PFX_PASSWORD` with the new PFX and its password
   (and update or remove `WINDOWS_CODESIGN_THUMBPRINT`).
2. The workflow detects that the certificate is not self-signed, then requires that **Windows itself reports the
   signature as `Valid`** (a trusted chain) instead of pinning a self-signed certificate. Everything else is unchanged.
3. Update the README and release notes wording about trust. Even then SmartScreen reputation builds over time and is
   not guaranteed.

Providers that sign in their own cloud (Azure Artifact Signing, SignPath, DigiCert KeyLocker, …) need their own signing
step and are intentionally not used here.

## Status

| | |
|---|---|
| Signing, verification and checksum pipeline | Implemented in `release.yml`; tooling tested by CI with a throwaway certificate |
| `WINDOWS_CODESIGN_PFX_BASE64`, `WINDOWS_CODESIGN_PFX_PASSWORD` | **Owner action required** (quick start above) |
| Certificate type | Self-signed (not publicly trusted) |
| Signed releases published | None until the secrets exist: v0.0.7 and older are unsigned |
