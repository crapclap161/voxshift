<#
.SYNOPSIS
  Creates the persistent, self-signed Authenticode code-signing certificate for VoxShift releases.

.DESCRIPTION
  Run this ONCE, on Windows (Windows PowerShell 5.1 or PowerShell 7), on your own computer:
      powershell -ExecutionPolicy Bypass -File .\scripts\create-signing-certificate.ps1

  It creates a certificate with a new RSA-3072 key (Code Signing extended key usage, SHA-256), exports it as a
  password-protected PFX (AES-256) and writes three files to an output folder OUTSIDE the repository:
      VoxShift-codesign.pfx             the certificate WITH its private key. Secret. Back it up. Never commit it.
      VoxShift-codesign.cer             the public certificate only (safe to share and to commit)
      WINDOWS_CODESIGN_PFX_BASE64.txt   the .pfx as Base64: the value for the first GitHub secret. Delete after upload.
  It prints the certificate thumbprint (public information) and the exact next steps. It never prints the password
  or any key material, never uploads anything and never touches git. The key is removed from your Windows certificate
  store afterwards, so the exported PFX is the only copy.

  The certificate is SELF-SIGNED: it proves that files were signed by the holder of this key and that they were not
  modified, but Windows does not trust it by itself and SmartScreen warnings can still appear. See
  docs/code-signing.md. The same GitHub secrets work unchanged with a publicly trusted certificate later.

.PARAMETER OutputDirectory
  Where to write the files. Default: Documents\VoxShift-signing. Must not be inside a git repository.

.PARAMETER Years
  Validity in years (default 10). Renewal means a NEW identity, so a long lifetime avoids frequent changes.

.PARAMETER Password
  PFX password as a SecureString. If omitted you are asked for it (recommended; use at least 16 characters, e.g. a
  passphrase from a password manager).

.PARAMETER Clipboard
  Also copy the Base64 value to the clipboard (be aware of clipboard history tools). Off by default.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'VoxShift-signing'),
    [string] $Subject = 'CN=VoxShift release signing (self-signed), O=VoxShift project (github.com/crapclap161/voxshift)',
    [ValidateRange(1, 30)] [int] $Years = 10,
    [securestring] $Password,
    [switch] $Clipboard,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) { throw 'This script must run on Windows (it uses the Windows certificate APIs).' }

# Never write key material inside a git working tree.
$resolved = [IO.Path]::GetFullPath($OutputDirectory)
for ($dir = New-Object IO.DirectoryInfo $resolved; $dir; $dir = $dir.Parent) {
    if (Test-Path (Join-Path $dir.FullName '.git')) { throw "Refusing to write signing keys inside a git repository ($($dir.FullName)). Choose a folder outside it, e.g. your Documents folder." }
}

$pfxPath = Join-Path $resolved 'VoxShift-codesign.pfx'
$cerPath = Join-Path $resolved 'VoxShift-codesign.cer'
$b64Path = Join-Path $resolved 'WINDOWS_CODESIGN_PFX_BASE64.txt'
if (-not $Force -and ((Test-Path $pfxPath) -or (Test-Path $cerPath) -or (Test-Path $b64Path))) {
    throw "Files already exist in $resolved. A signing certificate must stay the same across releases: do not create a second one unless you are rotating it on purpose (docs/code-signing.md). Use -Force to overwrite."
}

if (-not $Password) {
    $Password = Read-Host -AsSecureString -Prompt 'Choose a password for the PFX (16+ characters; you will store it as a GitHub secret)'
    $confirm = Read-Host -AsSecureString -Prompt 'Repeat the password'
    $plain1 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password))
    $plain2 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($confirm))
    $same = $plain1 -ceq $plain2
    $long = $plain1.Length -ge 16
    Remove-Variable plain1, plain2, confirm
    if (-not $same) { throw 'The passwords do not match.' }
    if (-not $long) { throw 'The password is too short (minimum 16 characters).' }
}

New-Item -ItemType Directory -Force -Path $resolved | Out-Null
$cert = $null
try {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 -KeyUsage DigitalSignature -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears($Years) -CertStoreLocation 'Cert:\CurrentUser\My'

    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $Password -CryptoAlgorithmOption AES256_SHA256 -Force | Out-Null
    Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT -Force | Out-Null
    [IO.File]::WriteAllText($b64Path, [Convert]::ToBase64String([IO.File]::ReadAllBytes($pfxPath)))
    if ($Clipboard) { Set-Clipboard -Value (Get-Content $b64Path -Raw) }

    # Restrict the secret files to the current user.
    foreach ($secret in $pfxPath, $b64Path) {
        $acl = Get-Acl $secret
        $acl.SetAccessRuleProtection($true, $false)
        $acl.Access | ForEach-Object { $acl.RemoveAccessRule($_) | Out-Null }
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule([Security.Principal.WindowsIdentity]::GetCurrent().Name, 'FullControl', 'Allow')))
        Set-Acl $secret $acl
    }
    $thumbprint = $cert.Thumbprint
    $notAfter = $cert.NotAfter.ToString('yyyy-MM-dd')
} finally {
    # The exported PFX is the only copy of the key; do not leave a second one in the Windows store.
    if ($cert) { Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host 'Certificate created.' -ForegroundColor Green
Write-Host "  Folder      : $resolved"
Write-Host "  Subject     : $Subject"
Write-Host "  Thumbprint  : $thumbprint   (public; the signature of every release will show this)"
Write-Host "  Valid until : $notAfter"
Write-Host ''
Write-Host 'NEXT STEPS' -ForegroundColor Cyan
Write-Host '  1. BACK UP VoxShift-codesign.pfx and the password NOW (e.g. a password manager attachment plus an offline copy).'
Write-Host '     If you lose either, future releases need a new certificate, i.e. a new signing identity.'
Write-Host '  2. On GitHub open: Settings > Secrets and variables > Actions > New repository secret, and add TWO secrets:'
Write-Host '       WINDOWS_CODESIGN_PFX_BASE64    = the entire content of WINDOWS_CODESIGN_PFX_BASE64.txt'
Write-Host '       WINDOWS_CODESIGN_PFX_PASSWORD  = the password you just chose'
Write-Host '     (With the GitHub CLI you can instead run, from any folder:'
Write-Host "        Get-Content -Raw '$b64Path' | gh secret set WINDOWS_CODESIGN_PFX_BASE64 --repo crapclap161/voxshift"
Write-Host '        gh secret set WINDOWS_CODESIGN_PFX_PASSWORD --repo crapclap161/voxshift      (it prompts; paste the password)'
Write-Host '     )'
Write-Host '  3. Delete WINDOWS_CODESIGN_PFX_BASE64.txt after uploading. Keep only the .pfx backup (VoxShift-codesign.cer is public).'
Write-Host '  4. Never commit any of these files. Then run the Release workflow (docs/code-signing.md).'
Write-Host ''
Write-Host 'This certificate is self-signed: it is not trusted by Windows and does not remove SmartScreen warnings.' -ForegroundColor Yellow
