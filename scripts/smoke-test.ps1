<#
.SYNOPSIS
  Launches the real VoxShift executable in --smoke-test mode and validates its report.

.DESCRIPTION
  The app starts its UI and audio engine, enumerates devices and applications, toggles the mode,
  probes process-loopback capture of its own process, plays a soundboard file, checks talk over music and
  soundboard auto-mute, shows the docked soundboard, switches to VR Controller Mode and back (verifying that the
  audio engine is never restarted), writes a report and exits. Build machines have
  no microphone or virtual cable, so only device-independent results are asserted.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ExePath,
    [Parameter(Mandatory)] [string] $ExpectedVersion,
    [int] $TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) ("voxshift-smoke-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$env:VOXSHIFT_SETTINGS_DIR = Join-Path $work 'settings'
$report = Join-Path $work 'smoke.txt'

# The Windows audio services are disabled on hosted runners; start them so the engine sees a real audio stack.
foreach ($svc in 'AudioEndpointBuilder', 'Audiosrv') {
    try { Set-Service -Name $svc -StartupType Automatic; Start-Service -Name $svc } catch { Write-Warning "Could not start ${svc}: $_" }
}

$p = Start-Process -FilePath $ExePath -ArgumentList '--smoke-test', "`"$report`"" -PassThru
$null = $p.Handle # keep a handle so ExitCode is available after exit
if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { $p.Kill(); throw "Smoke test timed out after $TimeoutSeconds s." }

Write-Host '---- smoke test report ----'
if (-not (Test-Path $report)) { throw 'The application did not write a smoke test report.' }
$text = Get-Content $report -Raw
Write-Host $text
Write-Host '---- application log ----'
Get-Content (Join-Path $env:LOCALAPPDATA 'VoxShift\logs\VoxShift.log') -ErrorAction SilentlyContinue | Select-Object -Last 40

if ($p.ExitCode -ne 0) { throw "Smoke test failed with exit code $($p.ExitCode)." }
if ($text -notmatch '(?m)^OK\s*$') { throw 'Report does not start with OK.' }
if ($text -notmatch "(?m)^Version: $([regex]::Escape($ExpectedVersion))\s*$") { throw "Report does not show version $ExpectedVersion." }
if ($text -notmatch '(?m)^Window title: VoxShift') { throw 'Window title is not branded VoxShift.' }
if ($text -notmatch 'Process loopback probe \(own process\): OK') { throw 'Process loopback capture probe failed.' }
if ($text -notmatch 'Soundboard probe: OK') { throw 'Soundboard probe failed (import, decode, resample or mixer).' }
if ($text -notmatch 'Smart mixing probe: OK') { throw 'Smart mixing probe failed (talk over music / soundboard auto-mute).' }
if ($text -notmatch 'Interface probe: OK') { throw 'Interface probe failed (soundboard dock or VR Controller Mode).' }
Write-Host 'Smoke test passed.'
