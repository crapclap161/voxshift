<#
.SYNOPSIS
  End-to-end test of the signing tooling with a THROWAWAY certificate (no repository secrets involved).

.DESCRIPTION
  Used by CI (pull requests included). It creates a temporary self-signed certificate with
  create-signing-certificate.ps1, signs a copy of the published executable with sign-executable.ps1 through the same
  environment-variable interface the release uses, and proves the gates behave:
    * the signed file verifies (integrity + pinned certificate) and still passes the smoke test
    * an unsigned file, a tampered signed file, a wrong pinned certificate and a wrong password are all rejected
    * a self-signed signature is not accepted without -TrustSelfSigned and never as publicly trusted
  The throwaway certificate and key are removed afterwards. Timestamping is NOT exercised here (it needs a network
  service); the release workflow requires it. Run from Windows PowerShell 5.1 or PowerShell 7 as administrator
  (GitHub's Windows runners are).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ExePath,
    [Parameter(Mandatory)] [string] $ExpectedVersion
)
$ErrorActionPreference = 'Stop'
$scripts = $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) ("voxshift-signtest-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

function Assert-Rejected([string] $What, [scriptblock] $Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true; Write-Host "  rejected as expected ($What): $($_.Exception.Message.Split("`n")[0])" }
    if (-not $rejected) { throw "FAILED: $What was accepted." }
}

try {
    # 1. Throwaway certificate (random password, never printed).
    $plain = [Convert]::ToBase64String((1..24 | ForEach-Object { Get-Random -Maximum 256 }))
    $secure = ConvertTo-SecureString $plain -AsPlainText -Force
    & (Join-Path $scripts 'create-signing-certificate.ps1') -OutputDirectory (Join-Path $work 'cert') -Password $secure -Years 1 -Subject 'CN=VoxShift CI throwaway certificate' | Out-Null
    $pfx = Join-Path $work 'cert\VoxShift-codesign.pfx'
    if (-not (Test-Path $pfx)) { throw 'Certificate script did not produce a PFX.' }
    $thumb = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @($pfx, $secure)).Thumbprint
    if (-not (Select-String -Path (Join-Path $work 'cert\WINDOWS_CODESIGN_PFX_BASE64.txt') -Pattern '^[A-Za-z0-9+/=]+$' -Quiet)) { throw 'Base64 file is malformed.' }

    # 2. Sign a copy through the release interface (environment variables).
    $signed = Join-Path $work 'VoxShift.exe'
    Copy-Item $ExePath $signed
    $env:WINDOWS_CODESIGN_PFX_BASE64 = (Get-Content (Join-Path $work 'cert\WINDOWS_CODESIGN_PFX_BASE64.txt') -Raw)
    $env:WINDOWS_CODESIGN_PFX_PASSWORD = $plain
    Write-Host '== validate + sign'
    & (Join-Path $scripts 'sign-executable.ps1') -ValidateOnly
    & (Join-Path $scripts 'sign-executable.ps1') -Path $signed -NoTimestamp
    if (Test-Path (Join-Path ([IO.Path]::GetTempPath()) 'voxshift-signing-state.txt')) { throw 'Signing left its state file behind.' }
    if (Get-ChildItem Cert:\CurrentUser\My | Where-Object Thumbprint -eq $thumb) { throw 'Signing certificate was not removed from the store.' }
    if (Get-ChildItem ([IO.Path]::GetTempPath()) -Filter 'voxshift-*.pfx' -ErrorAction SilentlyContinue) { throw 'A temporary PFX was left behind.' }

    # 3. Positive: integrity + identity.
    Write-Host '== verify signed file'
    & (Join-Path $scripts 'verify-signature.ps1') -Path $signed -ExpectedThumbprint $thumb -TrustSelfSigned
    if (Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -eq $thumb) { throw 'The temporary trusted root was not removed.' }

    # 4. The signed single-file executable still runs.
    Write-Host '== smoke test of the signed executable'
    & (Join-Path $scripts 'smoke-test.ps1') -ExePath $signed -ExpectedVersion $ExpectedVersion

    # 5. Negative tests.
    Write-Host '== negative tests'
    Assert-Rejected 'unsigned file' { & (Join-Path $scripts 'verify-signature.ps1') -Path $ExePath }
    Assert-Rejected 'unsigned file with pinned certificate' { & (Join-Path $scripts 'verify-signature.ps1') -Path $ExePath -ExpectedThumbprint $thumb -TrustSelfSigned }
    Assert-Rejected 'self-signed signature without -TrustSelfSigned' { & (Join-Path $scripts 'verify-signature.ps1') -Path $signed -ExpectedThumbprint $thumb }
    Assert-Rejected 'self-signed signature as publicly trusted' { & (Join-Path $scripts 'verify-signature.ps1') -Path $signed -ExpectedThumbprint $thumb -RequirePublicTrust }
    Assert-Rejected 'wrong expected certificate' { & (Join-Path $scripts 'verify-signature.ps1') -Path $signed -ExpectedThumbprint ('0' * 40) -TrustSelfSigned }
    Assert-Rejected 'missing timestamp when required' { & (Join-Path $scripts 'verify-signature.ps1') -Path $signed -ExpectedThumbprint $thumb -TrustSelfSigned -RequireTimestamp }
    $tampered = Join-Path $work 'tampered.exe'
    Copy-Item $signed $tampered
    $fs = [IO.File]::Open($tampered, 'Open', 'ReadWrite')
    try { $fs.Position = 1MB; $b = $fs.ReadByte(); $fs.Position = 1MB; $fs.WriteByte([byte](($b + 1) % 256)) } finally { $fs.Dispose() }
    Assert-Rejected 'modified signed file' { & (Join-Path $scripts 'verify-signature.ps1') -Path $tampered -ExpectedThumbprint $thumb -TrustSelfSigned }
    if (Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -eq $thumb) { throw 'A trusted root was left behind after a failed verification.' }
    $env:WINDOWS_CODESIGN_PFX_PASSWORD = 'definitely-not-the-password'
    Assert-Rejected 'wrong PFX password' { & (Join-Path $scripts 'sign-executable.ps1') -ValidateOnly }
    $env:WINDOWS_CODESIGN_PFX_PASSWORD = $plain
    Assert-Rejected 'pinned thumbprint mismatch when signing' { & (Join-Path $scripts 'sign-executable.ps1') -ValidateOnly -ExpectedThumbprint ('1' * 40) }
    Remove-Item Env:\WINDOWS_CODESIGN_PFX_BASE64
    Assert-Rejected 'missing signing secrets' { & (Join-Path $scripts 'sign-executable.ps1') -ValidateOnly }

    Write-Host 'Signing tooling test passed.'
} finally {
    Remove-Item Env:\WINDOWS_CODESIGN_PFX_BASE64, Env:\WINDOWS_CODESIGN_PFX_PASSWORD -ErrorAction SilentlyContinue
    & (Join-Path $scripts 'sign-executable.ps1') -Cleanup | Out-Null
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
