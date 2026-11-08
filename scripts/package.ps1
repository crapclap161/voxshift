<#
.SYNOPSIS
  Packages a published VoxShift build into the release ZIP and writes SHA-256 checksums.

.DESCRIPTION
  Used by both the CI and the release workflow so that every build is packaged identically.
  File timestamps inside the ZIP are set to the commit time (SOURCE_DATE_EPOCH when set) so that
  repeated packaging of the same commit yields the same archive layout and metadata.

.EXAMPLE
  ./scripts/package.ps1 -Version 0.0.1 -PublishDir publish -OutputDir dist
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $PublishDir,
    [Parameter(Mandatory)] [string] $OutputDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$name = "VoxShift-v$Version-win-x64"
$staging = Join-Path $OutputDir $name

$exe = Join-Path $PublishDir 'VoxShift.exe'
if (-not (Test-Path $exe)) { throw "Published executable not found: $exe" }

# Version resources of the single-file host are only written by Windows builds, so verify them there.
if ($IsWindows) {
    $fileVersion = (Get-Item $exe).VersionInfo.ProductVersion
    if (-not $fileVersion -or -not $fileVersion.StartsWith($Version)) { throw "VoxShift.exe reports version '$fileVersion', expected '$Version'." }
}

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Copy-Item $exe $staging
foreach ($doc in 'README.md', 'CHANGELOG.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
    Copy-Item (Join-Path $root $doc) $staging
}

# Normalise timestamps for reproducible archives.
$epoch = $env:SOURCE_DATE_EPOCH
if (-not $epoch) { $epoch = (git -C $root log -1 --format=%ct).Trim() }
$stamp = [DateTimeOffset]::FromUnixTimeSeconds([long]$epoch).UtcDateTime
Get-ChildItem $staging -File | ForEach-Object { $_.LastWriteTimeUtc = $stamp }

$zip = Join-Path $OutputDir "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name.zip" | Out-File -Encoding ascii -NoNewline (Join-Path $OutputDir 'SHA256SUMS.txt')

Write-Host "Packaged $zip"
Write-Host "SHA256  $hash"
if ($env:GITHUB_OUTPUT) {
    "zip=$zip" >> $env:GITHUB_OUTPUT
    "name=$name" >> $env:GITHUB_OUTPUT
}
