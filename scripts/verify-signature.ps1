<#
.SYNOPSIS
  Verifies that Windows executables carry a valid, timestamped, publicly trusted Authenticode signature.

.DESCRIPTION
  Used by the release workflow (and by CI as a negative test) so that an unsigned, self-signed, untrusted or
  un-timestamped binary can never be published. Fails with a non-zero exit code on the first problem.

  Checks per file:
    * WinVerifyTrust (Get-AuthenticodeSignature) reports Status = Valid, i.e. the signature matches the file and the
      certificate chains to a root that is trusted by Windows (a self-signed certificate is therefore rejected)
    * the signer certificate is not self-issued and has the Code Signing extended key usage
    * the signature carries an RFC 3161 timestamp countersignature
    * signtool verify /pa /all (when signtool.exe is available, e.g. on GitHub's Windows runners)
    * optionally, the signer subject contains -ExpectedSubject (pins the publisher identity)

.EXAMPLE
  ./scripts/verify-signature.ps1 -Path publish/VoxShift.exe -ExpectedSubject "CN=Jane Doe"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $Path,
    [string] $ExpectedSubject = ''
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') { throw 'Authenticode verification requires Windows.' }

$codeSigningEku = '1.3.6.1.5.5.7.3.3'
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1

foreach ($file in $Path) {
    if (-not (Test-Path $file)) { throw "File not found: $file" }
    $sig = Get-AuthenticodeSignature -FilePath $file
    $name = Split-Path -Leaf $file

    if ($sig.Status -ne 'Valid') { throw "$name : Authenticode status is '$($sig.Status)' ($($sig.StatusMessage)). Expected 'Valid' (signed and chained to a trusted root)." }
    $cert = $sig.SignerCertificate
    if (-not $cert) { throw "$name : no signer certificate." }
    if ($cert.Subject -eq $cert.Issuer) { throw "$name : signer certificate is self-signed ($($cert.Subject))." }
    if (-not ($cert.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq $codeSigningEku })) { throw "$name : signer certificate lacks the Code Signing EKU." }
    if (-not $sig.TimeStamperCertificate) { throw "$name : signature has no timestamp countersignature." }
    if ($ExpectedSubject -and -not $cert.Subject.Contains($ExpectedSubject)) { throw "$name : signer '$($cert.Subject)' does not contain the expected publisher '$ExpectedSubject'." }

    if ($signtool) {
        & $signtool.FullName verify /pa /all /v $file | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "$name : signtool verify /pa failed (exit code $LASTEXITCODE)." }
    } else {
        Write-Warning 'signtool.exe not found; relying on WinVerifyTrust result only.'
    }

    Write-Host "OK  $name"
    Write-Host "    signer     : $($cert.Subject)"
    Write-Host "    issuer     : $($cert.Issuer)"
    Write-Host "    thumbprint : $($cert.Thumbprint)"
    Write-Host "    timestamp  : $($sig.TimeStamperCertificate.Subject)"
}
