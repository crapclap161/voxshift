# Code signing (maintainer guide)

VoxShift releases are signed with **Authenticode** using **[Microsoft Artifact Signing](https://learn.microsoft.com/azure/artifact-signing/)**
(formerly *Trusted Signing*), authenticated from GitHub Actions with **OpenID Connect (OIDC)**. This page explains the
design and the one-time owner setup. **Nothing here has been configured for this repository yet**, so the release
workflow currently refuses to publish (see [Status](#status)).

## What signing does and does not do

- It proves who published `VoxShift.exe` (the *publisher* shown in the Windows file properties and the SmartScreen
  dialog) and that the file was not modified after signing. The signature carries a trusted timestamp, so it stays
  valid after the short-lived signing certificate expires.
- It does **not** guarantee that Windows SmartScreen never warns. SmartScreen also weighs download reputation, which
  builds up over time, so *“Windows protected your PC”* can still appear for new releases. Do not promise otherwise to
  users.
- A **self-signed** certificate, a GPG key or an SSH key does **not** help: Windows does not trust them. The pipeline
  rejects self-signed certificates on purpose.

## Why Artifact Signing + OIDC

| Requirement | How it is met |
|---|---|
| Publicly trusted certificate | Artifact Signing issues certificates from Microsoft's public-trust chain after **identity validation**. |
| No private key to leak | The key lives in Microsoft's HSM-backed service. The repository never sees a key, a `.pfx` or a password. |
| No long-lived secret | GitHub issues a short-lived OIDC token per run; Entra ID exchanges it for an Azure token. Only non-secret **IDs** are stored (as repository *variables*). |
| Only releases can sign | The Entra federated credential trusts only the GitHub *environment* `release-signing` of this repository, and pull requests never reach it. |
| Reproducible, auditable | The actions are pinned by commit SHA; every signature is timestamped (RFC 3161) and verified automatically. |

**Alternatives** (not implemented): a code-signing certificate from a public CA (OV or EV). Since 2023 the private key
must live on a hardware token or in a cloud HSM, which makes unattended CI signing harder and usually adds a
vendor-specific action. The [SignPath Foundation](https://signpath.org/) offers free signing for qualifying open-source
projects after an application. Artifact Signing is the simplest fit for a GitHub-hosted pipeline.

## What the pipeline does

`.github/workflows/release.yml` (job `release`, environment `release-signing`):

1. **Require code-signing configuration**: fails immediately if any variable below is missing. No unsigned release can
   be published.
2. Build, format-check, test and `dotnet publish` (self-contained single file).
3. `azure/login` with OIDC, then `azure/artifact-signing-action` signs `publish/VoxShift.exe` (SHA-256, timestamp from
   `http://timestamp.acs.microsoft.com`).
4. `scripts/verify-signature.ps1` checks that Windows reports the signature as **Valid** (so it chains to a trusted root),
   the signer is not self-signed, it has the Code Signing EKU, a timestamp is present, `signtool verify /pa` passes and,
   if configured, the publisher matches.
5. The smoke test runs the **signed** executable.
6. `scripts/package.ps1` creates the portable EXE, the ZIP and `SHA256SUMS.txt` **after** signing.
7. The signature of the portable EXE **and** of the EXE inside the ZIP is verified again, both files must be
   byte-identical, and `SHA256SUMS.txt` must contain exactly the checksums of the files about to be uploaded.
8. Only then is the GitHub Release created.

Ordinary CI (`ci.yml`, pull requests) never signs and has no access to the signing environment. It also runs a
negative test proving the verification script rejects an unsigned build.

## One-time owner setup

These steps need your Azure account, identity verification and (small) billing, so they cannot be automated.

1. **Azure subscription.** Create or use a pay-as-you-go subscription. Register the resource provider
   `Microsoft.CodeSigning`.
2. **Create an Artifact Signing account** (Azure portal → *Artifact Signing accounts*) in a region close to you. Note
   the **account name** and the region's **endpoint**, e.g. `https://eus.codesigning.azure.net/`
   ([list of endpoints](https://learn.microsoft.com/azure/artifact-signing/quickstart)). Pricing is a small monthly fee
   (a Basic tier exists); check the current price on the Azure pricing page.
3. **Identity validation** (*Identity validations* → *New identity* → **Public**). Microsoft verifies either an
   organization or, where offered, an individual developer with a government ID. Eligibility depends on country and
   account type and is subject to change, so check the
   [current requirements](https://learn.microsoft.com/azure/artifact-signing/quickstart) first. Validation can take
   days. The validated name becomes the publisher name users see.
4. **Create a certificate profile** of type **Public Trust** that uses the validated identity. Note the **profile name**.
5. **Create an Entra app registration** for GitHub (Entra ID → *App registrations* → *New*). Note its **Application
   (client) ID** and the **Directory (tenant) ID**.
6. **Add a federated credential** to that app (*Certificates & secrets* → *Federated credentials* → *GitHub Actions
   deploying Azure resources*):
   - Organization: `crapclap161`, Repository: `voxshift`
   - Entity type: **Environment**, name: `release-signing`
   - (Resulting subject: `repo:crapclap161/voxshift:environment:release-signing`)
7. **Grant the app permission to sign:** Artifact Signing account → *Access control (IAM)* → *Add role assignment* →
   **Artifact Signing Certificate Profile Signer** → select the app registration. (`azure/login` also needs the
   service principal to have a role on the *subscription* for the login step to succeed; *Reader* is enough.)
8. **GitHub environment.** In the repository: *Settings → Environments → New environment* → `release-signing`. Optionally
   add *Required reviewers* (you) so every release needs a manual approval, and restrict it to tags `v*`.
9. **Repository variables** (*Settings → Secrets and variables → Actions → Variables*; they are identifiers, not
   secrets):

   | Variable | Value |
   |---|---|
   | `AZURE_CLIENT_ID` | Application (client) ID from step 5 |
   | `AZURE_TENANT_ID` | Directory (tenant) ID from step 5 |
   | `AZURE_SUBSCRIPTION_ID` | The subscription ID from step 1 |
   | `SIGNING_ENDPOINT` | Endpoint from step 2, e.g. `https://eus.codesigning.azure.net/` |
   | `SIGNING_ACCOUNT` | Artifact Signing account name |
   | `SIGNING_CERTIFICATE_PROFILE` | Certificate profile name from step 4 |
   | `SIGNING_PUBLISHER` | *(optional, recommended)* text the signer subject must contain, e.g. `CN=Your Validated Name` |

   No GitHub **secrets** are required. If you ever see a request to store a password, client secret or `.pfx`, do not:
   it is not needed with this design.
10. **Release.** Follow the normal release process (README → *Releases* / `CONTRIBUTING.md`). The workflow signs,
    verifies and publishes. Afterwards download the portable EXE and check *Properties → Digital Signatures*, or run
    `Get-AuthenticodeSignature .\VoxShift-v….exe | Format-List`.

## Maintenance

- **Certificates rotate automatically.** Artifact Signing issues short-lived certificates per signing operation; the
  timestamp keeps old signatures valid. Nothing to renew in GitHub.
- **Identity validation** must be kept current in the Azure portal (Microsoft emails before it lapses).
- **Action updates.** Dependabot proposes updates for `azure/login` and `azure/artifact-signing-action`. When updating,
  keep the commit-SHA pin and verify the SHA belongs to the tagged release.
- **Rotating the app:** create a new federated credential, update `AZURE_CLIENT_ID`, delete the old app.
- **Local verification:** `./scripts/verify-signature.ps1 -Path VoxShift.exe` (Windows PowerShell 7 or Windows
  PowerShell 5.1 with `Get-AuthenticodeSignature`).

## Status

| | |
|---|---|
| Pipeline (signing, verification, checksums after signing) | Implemented in `release.yml` |
| Azure account, identity validation, certificate profile, Entra app, repository variables | **Not configured yet; owner action required** |
| Signed releases published | **None.** v0.0.7 and older are unsigned. v0.0.8 is published only once signing is configured and verified |
