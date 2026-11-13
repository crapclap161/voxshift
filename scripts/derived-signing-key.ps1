<#
.SYNOPSIS
  Helpers (dot-source this file) that turn a passphrase into a persistent self-signed code-signing identity.

.DESCRIPTION
  Used when the release has only a passphrase secret (VOXSHIFT_SIGNING_KEY) and no certificate file:
    passphrase --PBKDF2-HMAC-SHA256 (600,000 iterations, fixed project salt)--> 64 bytes --mod n--> ECDSA P-256 private key d
    d * G --> public key --> self-signed Code Signing certificate (generated in memory, never stored anywhere)
  The same passphrase always yields the same KEY, so the signing identity is stable across releases. The identity to
  compare is the public-key fingerprint (SHA-256 of the public key bytes): the certificate wrapper is rebuilt on each run
  (new serial number and signature), so its thumbprint is NOT stable.

  SECURITY NOTE: the public key is embedded in every signed file, so anybody can try passphrases offline against it.
  The passphrase must therefore be long and random (24+ random characters or 6+ random words). Anyone who learns it
  can sign as VoxShift. A stored random PFX (create-signing-certificate.ps1) avoids this and is stronger.
#>
Add-Type -AssemblyName System.Numerics
Add-Type -AssemblyName System.Security

$script:KeyDerivationSalt = 'VoxShift release signing key v1|github.com/crapclap161/voxshift'
$script:KeyDerivationIterations = 600000
$script:MinimumPassphraseLength = 16

# NIST P-256 domain parameters.
function ConvertFrom-Hex([string] $hex) { [System.Numerics.BigInteger]::Parse('0' + $hex, [Globalization.NumberStyles]::HexNumber) }
$script:P = ConvertFrom-Hex 'FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF'
$script:N = ConvertFrom-Hex 'FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551'
$script:Gx = ConvertFrom-Hex '6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296'
$script:Gy = ConvertFrom-Hex '4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5'

function Invoke-Mod([System.Numerics.BigInteger] $x) { $r = $x % $script:P; if ($r.Sign -lt 0) { $r + $script:P } else { $r } }
function Invoke-ModInverse([System.Numerics.BigInteger] $x) { [System.Numerics.BigInteger]::ModPow((Invoke-Mod $x), $script:P - 2, $script:P) }

# Affine point arithmetic on y^2 = x^3 - 3x + b; $null is the point at infinity.
function Add-CurvePoint($a, $b) {
    if ($null -eq $a) { return $b }
    if ($null -eq $b) { return $a }
    if ($a.X -eq $b.X) {
        if ((Invoke-Mod ($a.Y + $b.Y)).IsZero) { return $null }
        $slope = Invoke-Mod (($a.X * $a.X * 3 - 3) * (Invoke-ModInverse ($a.Y * 2)))
    } else {
        $slope = Invoke-Mod (($b.Y - $a.Y) * (Invoke-ModInverse ($b.X - $a.X)))
    }
    $x = Invoke-Mod ($slope * $slope - $a.X - $b.X)
    $y = Invoke-Mod ($slope * ($a.X - $x) - $a.Y)
    [pscustomobject]@{ X = $x; Y = $y }
}

function Get-PublicPoint([System.Numerics.BigInteger] $d) {
    $result = $null
    $addend = [pscustomobject]@{ X = $script:Gx; Y = $script:Gy }
    while ($d.Sign -gt 0) {
        if (-not $d.IsEven) { $result = Add-CurvePoint $result $addend }
        $addend = Add-CurvePoint $addend $addend
        $d = $d / 2
    }
    $result
}

function ConvertTo-BigEndian32([System.Numerics.BigInteger] $v) {
    $bytes = $v.ToByteArray()           # little-endian, may carry a sign byte
    [Array]::Reverse($bytes)
    $out = New-Object byte[] 32
    $take = [Math]::Min(32, $bytes.Length)
    [Array]::Copy($bytes, $bytes.Length - $take, $out, 32 - $take, $take)
    , $out
}

function ConvertFrom-BigEndian([byte[]] $bytes) {
    $le = New-Object byte[] ($bytes.Length + 1)   # trailing zero keeps the value positive
    for ($i = 0; $i -lt $bytes.Length; $i++) { $le[$i] = $bytes[$bytes.Length - 1 - $i] }
    New-Object System.Numerics.BigInteger -ArgumentList (, $le)
}

function Get-DerivedPrivateScalar([string] $Passphrase) {
    if ($null -eq $Passphrase -or $Passphrase.Length -lt $script:MinimumPassphraseLength) {
        throw "The signing passphrase must be at least $($script:MinimumPassphraseLength) characters (use a long random one)."
    }
    $kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes -ArgumentList @(
        [Text.Encoding]::UTF8.GetBytes($Passphrase), [Text.Encoding]::UTF8.GetBytes($script:KeyDerivationSalt),
        $script:KeyDerivationIterations, [Security.Cryptography.HashAlgorithmName]::SHA256)
    try { $seed = $kdf.GetBytes(64) } finally { $kdf.Dispose() }
    # 512 bits reduced into [1, n-1]: the modulo bias is about 2^-256.
    (ConvertFrom-BigEndian $seed) % ($script:N - 1) + 1
}

function Get-PublicKeySha256([Security.Cryptography.X509Certificates.X509Certificate2] $Certificate) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($Certificate.GetPublicKey())) -replace '-', '').ToLowerInvariant() } finally { $sha.Dispose() }
}

function New-DerivedSigningCertificate {
    <# Returns @{ Pfx = byte[]; PfxPassword = SecureString (random, for the in-memory/temporary PFX only) }. #>
    param([Parameter(Mandatory)] [string] $Passphrase,
          [string] $Subject = 'CN=VoxShift release signing (self-signed - passphrase-derived key), O=VoxShift project (github.com/crapclap161/voxshift)')

    $d = Get-DerivedPrivateScalar $Passphrase
    $q = Get-PublicPoint $d
    $ecParameters = New-Object Security.Cryptography.ECParameters
    $ecParameters.Curve = [Security.Cryptography.ECCurve+NamedCurves]::nistP256
    $ecParameters.D = ConvertTo-BigEndian32 $d
    $point = New-Object Security.Cryptography.ECPoint   # a struct: assign it whole, not field by field
    $point.X = ConvertTo-BigEndian32 $q.X
    $point.Y = ConvertTo-BigEndian32 $q.Y
    $ecParameters.Q = $point
    $ecdsa = [Security.Cryptography.ECDsa]::Create($ecParameters)
    try {
        $request = New-Object Security.Cryptography.X509Certificates.CertificateRequest -ArgumentList @($Subject, $ecdsa, [Security.Cryptography.HashAlgorithmName]::SHA256)
        $request.CertificateExtensions.Add((New-Object Security.Cryptography.X509Certificates.X509BasicConstraintsExtension -ArgumentList @($false, $false, 0, $true)))
        $request.CertificateExtensions.Add((New-Object Security.Cryptography.X509Certificates.X509KeyUsageExtension -ArgumentList @([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true)))
        $oids = New-Object Security.Cryptography.OidCollection
        [void] $oids.Add((New-Object Security.Cryptography.Oid -ArgumentList '1.3.6.1.5.5.7.3.3'))
        $request.CertificateExtensions.Add((New-Object Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension -ArgumentList @($oids, $false)))
        $request.CertificateExtensions.Add((New-Object Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension -ArgumentList @($request.PublicKey, $false)))
        # Fixed 20-year validity so the certificate never needs a "renewal" (a renewal would not change the key anyway).
        $cert = $request.CreateSelfSigned([DateTimeOffset]'2026-01-01T00:00:00Z', [DateTimeOffset]'2046-01-01T00:00:00Z')
        $random = New-Object byte[] 24
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create(); try { $rng.GetBytes($random) } finally { $rng.Dispose() }
        $pfxPassword = [Convert]::ToBase64String($random)
        $securePfxPassword = ConvertTo-SecureString -String $pfxPassword -AsPlainText -Force
        $pfx = $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $securePfxPassword)
        $cert.Dispose()
        @{ Pfx = $pfx; PfxPassword = $securePfxPassword }
    } finally { $ecdsa.Dispose() }
}
