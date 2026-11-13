<#
.SYNOPSIS
  Verifies the Authenticode signature of Windows executables and reports, separately, what is and is not established.

.DESCRIPTION
  Three different questions are kept apart (see docs/code-signing.md):
    1. Integrity     - is the signature cryptographically valid for exactly this file (no tampering)?
    2. Identity      - was it made with the EXPECTED certificate (-ExpectedThumbprint)?
    3. Public trust  - does the certificate chain to a root that Windows trusts on its own?
  SmartScreen reputation is a fourth, separate thing that no script can verify.

  A self-signed certificate (subject == issuer) can satisfy 1 and 2 but never 3. WinVerifyTrust only reports
  "Valid" when the chain is trusted, so for self-signed certificates the script uses -TrustSelfSigned: it adds the
  one expected certificate to the machine's Trusted Root store for the duration of the check (needs administrator
  rights; intended for ephemeral CI runners) and removes it again. Without -TrustSelfSigned a self-signed signature
  is NOT accepted, so nothing is silently weakened. For a publicly trusted certificate use -RequirePublicTrust (or
  neither switch): Windows must report Valid on its own.

  Always checked: the file is signed and unmodified (status is not NotSigned / HashMismatch), the signer has the Code
  Signing extended key usage, and (when given) the signer matches -ExpectedThumbprint and a timestamp is present
  (-RequireTimestamp). With full trust evaluation, `signtool verify /pa /all` must pass and the file digest must be
  SHA-256.

.EXAMPLE
  ./scripts/verify-signature.ps1 -Path VoxShift.exe -ExpectedThumbprint 0123... -RequireTimestamp -TrustSelfSigned
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $Path,
    [string] $ExpectedThumbprint = '',
    [switch] $RequireTimestamp,
    [switch] $TrustSelfSigned,
    [switch] $RequirePublicTrust
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) { throw 'Authenticode verification requires Windows.' }
Add-Type -AssemblyName System.Security
$X509 = 'System.Security.Cryptography.X509Certificates'
$codeSigningEku = '1.3.6.1.5.5.7.3.3'
$expected = ($ExpectedThumbprint -replace '\s', '').ToUpperInvariant()

if ($TrustSelfSigned -and $RequirePublicTrust) { throw '-TrustSelfSigned and -RequirePublicTrust are mutually exclusive.' }
if ($TrustSelfSigned -and -not $expected) { throw '-TrustSelfSigned requires -ExpectedThumbprint: only one known certificate may be trusted.' }

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1

$trustedRootStore = $null
$addedToRoot = $null
try {
    foreach ($file in $Path) {
        if (-not (Test-Path $file)) { throw "File not found: $file" }
        $name = Split-Path -Leaf $file
        $sig = Get-AuthenticodeSignature -FilePath $file

        # --- 1. Integrity ---------------------------------------------------------------------------------------
        if ($sig.Status -eq 'NotSigned') { throw "$name : the file is not signed." }
        if ($sig.Status -eq 'HashMismatch') { throw "$name : the signature does not match the file (modified after signing)." }
        $cert = $sig.SignerCertificate
        if (-not $cert) { throw "$name : no signer certificate found." }

        $eku = @($cert.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
            ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value })
        if ($eku -notcontains $codeSigningEku) { throw "$name : the signer certificate lacks the Code Signing extended key usage." }

        # --- 2. Identity ----------------------------------------------------------------------------------------
        if ($expected -and $cert.Thumbprint.ToUpperInvariant() -ne $expected) {
            throw "$name : signed with certificate $($cert.Thumbprint), expected $expected."
        }

        if ($RequireTimestamp -and -not $sig.TimeStamperCertificate) { throw "$name : the signature has no RFC 3161 timestamp." }

        # --- 3. Public trust ------------------------------------------------------------------------------------
        $selfSigned = $cert.Subject -eq $cert.Issuer
        if ($selfSigned -and $RequirePublicTrust) { throw "$name : the signer certificate is self-signed, so it is not publicly trusted." }

        if ($selfSigned) {
            if (-not $TrustSelfSigned) {
                throw "$name : self-signed signature (status '$($sig.Status)'). Windows cannot confirm integrity for an untrusted root; re-run with -TrustSelfSigned -ExpectedThumbprint <thumbprint> (administrator)."
            }
            if (-not $trustedRootStore) {
                $trustedRootStore = New-Object "$X509.X509Store" 'Root', 'LocalMachine'
                $trustedRootStore.Open('ReadWrite')
            }
            if (-not $trustedRootStore.Certificates.Find('FindByThumbprint', $cert.Thumbprint, $false).Count) {
                $trustedRootStore.Add($cert)
                $addedToRoot = $cert
            }
            $sig = Get-AuthenticodeSignature -FilePath $file
        }
        if ($sig.Status -ne 'Valid') { throw "$name : Authenticode status is '$($sig.Status)' ($($sig.StatusMessage))." }

        if ($signtool) {
            $output = & $signtool.FullName verify /pa /all /v $file 2>&1 | Out-String
            Write-Verbose $output
            if ($LASTEXITCODE -ne 0) { throw "$name : signtool verify /pa failed (exit code $LASTEXITCODE).`n$output" }
            if ($output -notmatch '(?i)Hash of file \(sha256\)') { throw "$name : the file digest is not SHA-256.`n$output" }
        } else {
            Write-Warning 'signtool.exe not found; relying on the WinVerifyTrust result only.'
        }

        $timestamp = if ($sig.TimeStamperCertificate) { "yes ($($sig.TimeStamperCertificate.Subject))" } else { 'no' }
        Write-Host "OK  $name"
        Write-Host "    integrity     : valid (unmodified since signing)"
        Write-Host "    signer        : $($cert.Subject)"
        Write-Host "    thumbprint    : $($cert.Thumbprint)$(if ($expected) { '  (matches the expected certificate)' })"
        Write-Host "    timestamp     : $timestamp"
        if ($selfSigned) {
            Write-Host '    public trust  : NO. Self-signed certificate, accepted only because this exact certificate was pinned.'
        } else {
            Write-Host "    public trust  : yes (chains to a root trusted by Windows; issuer: $($cert.Issuer))"
        }
        Write-Host '    SmartScreen   : not evaluated; reputation is separate and cannot be verified here.'
    }
} finally {
    if ($trustedRootStore) {
        if ($addedToRoot) { try { $trustedRootStore.Remove($addedToRoot) } catch { Write-Warning "Could not remove the temporary trusted root: $($_.Exception.Message)" } }
        $trustedRootStore.Close()
    }
}
