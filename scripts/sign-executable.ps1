<#
.SYNOPSIS
  Authenticode-signs executables with the project's PFX code-signing certificate (SHA-256, RFC 3161 timestamp).

.DESCRIPTION
  The certificate comes from two environment variables (GitHub Actions secrets in the release workflow):
    WINDOWS_CODESIGN_PFX_BASE64    the .pfx file, Base64 encoded
    WINDOWS_CODESIGN_PFX_PASSWORD  its password
  Neither value is ever printed or passed on a command line. The PFX is decoded to a private temporary file only
  long enough to import it into the (ephemeral) user certificate store as a NON-exportable key; the file is deleted
  right after the import, and again in the cleanup. The key is removed from the store when signing is finished.

  Works for a self-signed certificate (the default, see docs/code-signing.md) and, unchanged, for a publicly trusted
  OV/EV code-signing certificate exported as PFX.

  Modes
    -ValidateOnly   check the secrets/certificate (decodes, password, private key, code-signing EKU, validity) and
                    write non-secret facts to $GITHUB_OUTPUT. Signs nothing. Used as an early release pre-flight.
    -Path <files>   sign the files with SignTool, then timestamp them.
    -Cleanup        remove the certificate/key and temporary files (safe to call repeatedly, e.g. `if: always()`).

.EXAMPLE
  $env:WINDOWS_CODESIGN_PFX_BASE64 = '...'; $env:WINDOWS_CODESIGN_PFX_PASSWORD = '...'
  ./scripts/sign-executable.ps1 -Path publish/VoxShift.exe
#>
[CmdletBinding()]
param(
    [string[]] $Path,
    [switch] $ValidateOnly,
    [switch] $Cleanup,
    # Timestamp authorities are tried in order. A release requires a timestamp, so signing fails if none responds.
    [string[]] $TimestampUrl = @('http://timestamp.digicert.com', 'http://timestamp.sectigo.com', 'http://timestamp.globalsign.com/tsa/r6advanced1'),
    # Only for automated tests that must not depend on the network. Never used by the release workflow.
    [switch] $NoTimestamp,
    # Optional pin: refuse to sign unless the certificate has this SHA-1 thumbprint.
    [string] $ExpectedThumbprint = $env:WINDOWS_CODESIGN_THUMBPRINT
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$X509 = 'System.Security.Cryptography.X509Certificates'
$codeSigningEku = '1.3.6.1.5.5.7.3.3'
$stateFile = Join-Path ([IO.Path]::GetTempPath()) 'voxshift-signing-state.txt'
$tempPfx = Join-Path ([IO.Path]::GetTempPath()) ("voxshift-" + [Guid]::NewGuid().ToString('N') + '.pfx')

function Remove-SigningCertificate {
    # Removes the certificate and its private key from the CurrentUser\My store and deletes temporary files.
    if (Test-Path $stateFile) {
        $thumbprints = @(Get-Content $stateFile | Where-Object { $_ })
        $store = New-Object "$X509.X509Store" 'My', 'CurrentUser'
        $store.Open('ReadWrite')
        try {
            foreach ($thumbprint in $thumbprints) {
                foreach ($cert in @($store.Certificates.Find('FindByThumbprint', $thumbprint, $false))) {
                    try {
                        $key = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
                        if ($key -is [Security.Cryptography.RSACng]) { $key.Key.Delete() }
                    } catch { Write-Verbose "Key container cleanup: $($_.Exception.Message)" }
                    $store.Remove($cert)
                }
            }
        } finally { $store.Close() }
        Remove-Item $stateFile -Force -ErrorAction SilentlyContinue
    }
    Get-ChildItem ([IO.Path]::GetTempPath()) -Filter 'voxshift-*.pfx' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}

if ($Cleanup) { Remove-SigningCertificate; Write-Host 'Signing certificate and temporary files removed.'; return }

$instructions = @'
Code signing is not configured. Add two GitHub Actions repository secrets (Settings > Secrets and variables > Actions):
  WINDOWS_CODESIGN_PFX_BASE64    the code-signing .pfx file, Base64 encoded
  WINDOWS_CODESIGN_PFX_PASSWORD  the password of that .pfx
Create them with scripts/create-signing-certificate.ps1 (see docs/code-signing.md). Refusing to produce an unsigned release.
'@
$pfxBase64 = $env:WINDOWS_CODESIGN_PFX_BASE64
$pfxPassword = $env:WINDOWS_CODESIGN_PFX_PASSWORD
if ([string]::IsNullOrWhiteSpace($pfxBase64) -or [string]::IsNullOrEmpty($pfxPassword)) { throw $instructions }

try { $pfxBytes = [Convert]::FromBase64String(($pfxBase64 -replace '\s', '')) }
catch { throw 'WINDOWS_CODESIGN_PFX_BASE64 is not valid Base64. Re-create it from the .pfx file (see docs/code-signing.md).' }
$securePassword = ConvertTo-SecureString -String $pfxPassword -AsPlainText -Force

# Validate in memory first (nothing touches the disk or the certificate store yet).
try {
    $probe = New-Object "$X509.X509Certificate2" -ArgumentList @($pfxBytes, $securePassword, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
} catch { throw 'The PFX could not be opened. Wrong WINDOWS_CODESIGN_PFX_PASSWORD, or WINDOWS_CODESIGN_PFX_BASE64 is not a .pfx file.' }

$thumbprint = $probe.Thumbprint
$eku = @($probe.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
    ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value })
$now = Get-Date
if (-not $probe.HasPrivateKey) { throw 'The PFX contains no private key.' }
if ($eku -notcontains $codeSigningEku) { throw 'The certificate does not have the Code Signing extended key usage (1.3.6.1.5.5.7.3.3).' }
if ($now -lt $probe.NotBefore) { throw "The certificate is not valid before $($probe.NotBefore.ToString('u'))." }
if ($now -gt $probe.NotAfter) { throw "The certificate expired on $($probe.NotAfter.ToString('u')). Create a new one (docs/code-signing.md)." }
if ($ExpectedThumbprint -and ($ExpectedThumbprint -replace '\s', '') -ne $thumbprint) {
    throw "The certificate thumbprint does not match WINDOWS_CODESIGN_THUMBPRINT. Is the right PFX stored in the secret?"
}
$daysLeft = [int]($probe.NotAfter - $now).TotalDays
if ($daysLeft -lt 60) { Write-Warning "The signing certificate expires in $daysLeft days. Plan a renewal (docs/code-signing.md)." }
$selfSigned = $probe.Subject -eq $probe.Issuer
$subject = $probe.Subject
$notAfter = $probe.NotAfter.ToString('yyyy-MM-dd')
$probe.Dispose()

Write-Host 'Signing certificate OK'
Write-Host "  subject    : $subject"
Write-Host "  thumbprint : $thumbprint"
Write-Host "  valid until: $notAfter ($daysLeft days)"
Write-Host "  self-signed: $selfSigned"
if ($env:GITHUB_OUTPUT) {
    "thumbprint=$thumbprint" >> $env:GITHUB_OUTPUT
    "subject=$subject" >> $env:GITHUB_OUTPUT
    "self_signed=$($selfSigned.ToString().ToLowerInvariant())" >> $env:GITHUB_OUTPUT
    "not_after=$notAfter" >> $env:GITHUB_OUTPUT
}
if ($ValidateOnly) { return }

if (-not $Path -or $Path.Count -eq 0) { throw 'Specify -Path with the file(s) to sign.' }
foreach ($file in $Path) { if (-not (Test-Path $file)) { throw "File not found: $file" } }

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe (Windows SDK) not found.' }

try {
    # Restore the PFX into a temporary file, import it with a NON-exportable key, delete the file immediately.
    [IO.File]::WriteAllBytes($tempPfx, $pfxBytes)
    $flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet -bor [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet
    $cert = New-Object "$X509.X509Certificate2" -ArgumentList @($tempPfx, $securePassword, $flags)
    Remove-Item $tempPfx -Force
    $store = New-Object "$X509.X509Store" 'My', 'CurrentUser'
    $store.Open('ReadWrite')
    try { $store.Add($cert) } finally { $store.Close() }
    Set-Content -Path $stateFile -Value $thumbprint
    Clear-Variable pfxBytes, pfxBase64, pfxPassword -ErrorAction SilentlyContinue

    foreach ($file in $Path) {
        $name = Split-Path -Leaf $file
        # Sign with SHA-256 (the key is selected by thumbprint, so no password appears on any command line).
        & $signtool.FullName sign /fd SHA256 /sha1 $thumbprint /s My /d 'VoxShift' /du 'https://github.com/crapclap161/voxshift' $file | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "signtool sign failed for $name (exit code $LASTEXITCODE)." }

        if ($NoTimestamp) { Write-Warning "$name signed WITHOUT a timestamp (test mode)."; continue }
        # Timestamp separately so that a flaky timestamp server can be retried / replaced without re-signing.
        $stamped = $false
        foreach ($url in $TimestampUrl) {
            foreach ($attempt in 1..2) {
                & $signtool.FullName timestamp /tr $url /td SHA256 $file | Out-Host
                if ($LASTEXITCODE -eq 0) { $stamped = $true; break }
                Start-Sleep -Seconds (3 * $attempt)
            }
            if ($stamped) { break }
        }
        if (-not $stamped) { throw "Could not timestamp $name with any timestamp server ($($TimestampUrl -join ', '))." }
        Write-Host "Signed and timestamped $name"
    }
} finally {
    Remove-SigningCertificate
}
