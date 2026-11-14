# Code signing (maintainer guide)

VoxShift releases are signed with **Authenticode** using a key that you control. The only thing stored in GitHub is **one
repository secret, `VOXSHIFT_SIGNING_KEY`** (a passphrase). There is no certificate file, no cloud signing service, no
Azure account and no external identity verification.

> [!IMPORTANT]
> The certificate is **self-signed**. Read [What a self-signed signature does and does not give you](#what-a-self-signed-signature-does-and-does-not-give-you)
> before telling users anything about it, and [Choosing the passphrase](#choosing-the-passphrase).

## Quick start

1. **Create the secret** at *Settings → Secrets and variables → Actions → New repository secret*:

   | Secret name | Value |
   |---|---|
   | `VOXSHIFT_SIGNING_KEY` | a long **random** passphrase (24+ random characters, e.g. from a password manager) |

   Keep a copy of the passphrase in a password manager. **It is the signing key**: lose it and future releases get a new
   identity; leak it and anybody can sign as VoxShift.
2. **Release:** run *Actions → Release → Run workflow* (tag `v0.0.9`) or push the tag. The first step validates the secret, so a
   missing secret fails within seconds with instructions, before anything is built or published.
3. **Note the key identity** printed by the run (*Signing key SHA-256*). It is also written into the release notes, and
   it is what users compare. It stays the same for every release signed with the same passphrase.

## How the signing key works

Windows only accepts signatures made with a *certificate*, so the workflow builds one from the passphrase on every run:

```
passphrase ──PBKDF2-HMAC-SHA256, 600,000 iterations, fixed project salt──▶ ECDSA P-256 private key
          ──▶ self-signed Code Signing certificate (created in memory) ──▶ signtool (SHA-256 + RFC 3161 timestamp)
```

- The same passphrase always gives the **same key**, so the signing identity persists across releases without storing a
  certificate or key anywhere (`scripts/derived-signing-key.ps1`).
- The certificate wrapper is rebuilt each run (new serial number and signature), so its *thumbprint* changes. The stable
  identity is the **public-key SHA-256** ("key ID"). The verification pins that, not the thumbprint.
- Nothing is written to disk except a short-lived temporary PFX that is imported with a non-exportable key and deleted at
  once; the key is removed from the runner afterwards, also when the job fails. The secret is exposed only to the validate
  and sign steps, passed through an environment variable (never a command line) and masked in logs.

### Choosing the passphrase

A normal certificate stores a random private key. Here the key is *calculated from the passphrase*, and every signed
file contains the matching public key, so anyone can test guesses against it offline. That makes the strength of the
passphrase the strength of the signing identity:

- Use **24+ random characters** or **6+ random words** (not a sentence, name or reused password). The scripts refuse
  anything shorter than 16 characters. With a truly random 24-character passphrase guessing is not feasible; with a
  human-chosen one it may be.
- Treat it like a private key: password manager, never in the repository, issues, chat or logs.
- If you want the stronger classic setup (a random key in a password-protected PFX) it is supported too; see
  [Using a certificate file instead](#using-a-certificate-file-instead).

## How the pipeline works

`.github/workflows/release.yml`:

1. **Validate signing key.** `scripts/sign-executable.ps1 -ValidateOnly` checks the secret exists and builds the identity
   in memory (key ID, Code Signing key usage, validity). Missing or invalid secrets stop the release.
2. Validate the tag against `Directory.Build.props`, `CHANGELOG.md`, release notes and README screenshots; restore,
   format check, build, unit tests; `dotnet publish` (self-contained single file).
3. **Sign** `publish/VoxShift.exe` (SHA-256) and add an **RFC 3161 timestamp** (DigiCert, with Sectigo and GlobalSign as
   fallbacks; no timestamp means no release).
4. **Verify** (`scripts/verify-signature.ps1`): the file is signed and unmodified, signed by the **expected key**, has a
   timestamp and a SHA-256 digest, and `signtool verify /pa` passes.
5. Smoke test of the **signed** executable, then packaging (portable EXE, ZIP, `SHA256SUMS.txt`) **after** signing.
6. Verify again the portable EXE **and** the EXE inside the ZIP, check that both are identical, and that
   `SHA256SUMS.txt` contains the checksums of the files being uploaded.
7. Create the GitHub Release. Its text gets a *Digital signature* section generated from the certificate (signer, key
   ID, self-signed or publicly trusted).

Ordinary CI (`ci.yml`, pull requests) never receives the secret. It runs `scripts/test-signing.ps1` with **throwaway**
keys: the key derivation is checked against .NET's own elliptic-curve maths, the same passphrase must give the same
key and another passphrase a different one; signed files must verify and run; and unsigned files, tampered files, a
wrong key, a wrong password, missing secrets and "self-signed as publicly trusted" are all rejected. (Timestamping needs
a network service, so only the release exercises it.) Another CI check fails if a key or PFX is ever committed.

## What a self-signed signature does and does not give you

Four different things are easy to confuse:

| | Self-signed (this setup) | Publicly trusted certificate |
|---|---|---|
| **Cryptographic integrity**: the file was not modified after signing | ✅ yes | ✅ yes |
| **Identity of the key holder**: signed with *this* key (compare the key ID) | ✅ yes, if the user knows the key ID | ✅ yes, and a CA vouched for the publisher name |
| **Public trust chain**: Windows trusts the certificate on its own | ❌ **no** | ✅ yes |
| **SmartScreen reputation** | ❌ **not improved** | ⚠️ not guaranteed either; builds up with downloads |

So with this key:

- Windows shows the publisher as unknown or untrusted and may still show *“Windows protected your PC”*.
- The signature is **not** a “verified publisher” and must not be described as such.
- `Get-AuthenticodeSignature` reports a status such as `UnknownError` or `NotTrusted` (untrusted root), *not* `Valid`.
  `HashMismatch` means the file was changed after signing.
- It is still useful: someone who knows the key ID can confirm that a file comes from the holder of the VoxShift
  release key and was not tampered with, and the identity stays the same across releases.
- Publishing the key ID (or the public key) does **not** make Windows trust it.

> Users should not be asked to install your certificate into their Trusted Root store. That would let anyone holding
> the key sign software that Windows then trusts on their machine.

### How to verify a downloaded file

1. **Checksum:** `Get-FileHash .\VoxShift-v0.0.9-win-x64-portable.exe -Algorithm SHA256` must equal the line in
   `SHA256SUMS.txt`.
2. **Signature and key ID:**

   ```powershell
   $s = Get-AuthenticodeSignature .\VoxShift-v0.0.9-win-x64-portable.exe
   $s.Status   # NotTrusted / UnknownError for a self-signed key is expected; HashMismatch or NotSigned is NOT ok
   $s.SignerCertificate.Subject
   # key ID: must equal the "Signing key SHA-256" in the release notes
   [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($s.SignerCertificate.GetPublicKey())).Replace('-', '').ToLower()
   $s.TimeStamperCertificate.Subject   # present = timestamped
   ```

3. **Strict check (maintainers, administrator PowerShell):** this temporarily trusts only the pinned key to let Windows
   verify the signature cryptographically, then removes it again:

   ```powershell
   .\scripts\verify-signature.ps1 -Path .\VoxShift.exe -ExpectedPublicKeySha256 <key id> -RequireTimestamp -TrustSelfSigned
   ```

   The output lists integrity, signer, key ID, timestamp and states plainly that a self-signed certificate is *not*
   publicly trusted.

To see your own key ID without releasing, set the passphrase in an environment variable and run
`$env:WINDOWS_CODESIGN_PFX_PASSWORD='<passphrase>'; .\scripts\sign-executable.ps1 -ValidateOnly` (it prints the key ID and
signs nothing; close the shell afterwards so the passphrase isn't kept).

## Backing up and rotating

- **Backup:** the passphrase is the whole key. Store it in a password manager *and* an offline copy. GitHub secrets cannot
  be read back, so the repository is not a backup.
- **Rotate** (leak, or you want a different identity): set a new random `VOXSHIFT_SIGNING_KEY`. The new key ID is a **new
  identity**; announce it in the next release notes. Old releases keep their valid signatures and timestamps. After a
  leak, replace the secret first, then say so in the release notes.
- The certificate's validity is fixed at 2026-01-01 to 2046-01-01; there is nothing to renew.

## Using a certificate file instead

Two optional extras use the same pipeline (set the repository secrets; no code change):

| Secret | Value |
|---|---|
| `WINDOWS_CODESIGN_PFX_BASE64` | the `.pfx`, Base64-encoded |
| `VOXSHIFT_SIGNING_KEY` | that PFX's **password** (alias: `WINDOWS_CODESIGN_PFX_PASSWORD`) |

- **A random stored key** (stronger than a derived one): create a self-signed certificate once with
  `scripts/create-signing-certificate.ps1` on Windows (password ≥ 16 characters; it writes the PFX, a public `.cer` and the
  Base64 text file *outside* the repository and prints next steps). `Get-Content -Raw <that .txt> | gh secret set WINDOWS_CODESIGN_PFX_BASE64`
  uploads it. Back up the PFX and its password, and delete the Base64 file afterwards. An optional repository variable
  `WINDOWS_CODESIGN_THUMBPRINT` pins the certificate.
- **A publicly trusted OV/EV certificate** that can be exported as PFX (many CAs issue keys on hardware tokens that cannot
  be exported; check before buying). The workflow detects that the certificate is not self-signed and then requires
  that **Windows itself reports the signature `Valid`** instead of pinning a self-signed key. Update the README and
  release-notes wording about trust; SmartScreen reputation still builds over time and is not guaranteed.

Providers that sign in their own cloud (Azure Artifact Signing, SignPath, DigiCert KeyLocker, …) need their own signing
step and are intentionally not used.

## Status

| | |
|---|---|
| Signing, verification and checksum pipeline | Implemented in `release.yml`; tooling tested by CI with throwaway keys |
| `VOXSHIFT_SIGNING_KEY` repository secret | Created by the owner |
| Key type | Self-signed, derived from the passphrase (not publicly trusted) |
| Signed releases published | v0.0.8 and v0.0.9 (key ID `35a1adba1e8754d9f490e38b22060acc04041e6794c5f51f085b4d867d07356e`). v0.0.7 and older are unsigned |
