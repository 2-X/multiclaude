# Installs (or updates) MultiClaude for Windows:
#   %LOCALAPPDATA%\Programs\MultiClaude\MultiClaude.exe   tray app
#   %LOCALAPPDATA%\Programs\MultiClaude\multiclaude.com   CLI (added to your PATH)
# plus a Start menu entry, launch at sign-in, the claude:// link router, and
# starts the tray. Builds from source first when run from a checkout
# (windows\build.ps1, no SDK needed).
[CmdletBinding()]
param([switch]$NoPath, [switch]$NoLogin, [switch]$NoStart, [switch]$NoRouter)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $env:LOCALAPPDATA 'Programs\MultiClaude'

if (Test-Path (Join-Path $here 'src')) {
  & (Join-Path $here 'build.ps1')
  $from = Join-Path $here 'build'
} else {
  $from = $here   # release zip: the binaries sit next to this script
}
foreach ($f in 'MultiClaude.exe', 'multiclaude.com') {
  if (-not (Test-Path (Join-Path $from $f))) { throw "missing $f in $from" }
}

# An older tray holds MultiClaude.exe open; it is ours to stop.
Get-Process MultiClaude -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($dest, 'OrdinalIgnoreCase') } |
  ForEach-Object { Write-Host "Stopping the running tray ($($_.Id))"; Stop-Process -Id $_.Id -Force; $_.WaitForExit(5000) | Out-Null }

New-Item -ItemType Directory -Force $dest | Out-Null
foreach ($f in 'MultiClaude.exe', 'multiclaude.com') {
  Copy-Item (Join-Path $from $f) (Join-Path $dest $f) -Force
  Unblock-File (Join-Path $dest $f)   # from a downloaded zip: no SmartScreen prompt at every start
}
Write-Host "Installed to $dest"

$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'MultiClaude.lnk'
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk)
$s.TargetPath = Join-Path $dest 'MultiClaude.exe'
$s.WorkingDirectory = $dest
$s.Description = 'Run several Claude desktop copies side by side'
$s.Save()
Write-Host "Start menu: $lnk"

if (-not $NoPath) {
  $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
  if (-not (($userPath -split ';') | Where-Object { $_.TrimEnd('\') -ieq $dest })) {
    [Environment]::SetEnvironmentVariable('Path', ($(if ($userPath) { $userPath.TrimEnd(';') + ';' } else { '' }) + $dest), 'User')
    Write-Host "Added $dest to your PATH (new terminals get the multiclaude command)"
  }
}

$cli = Join-Path $dest 'multiclaude.com'
if (-not $NoLogin) { & $cli login on --quiet | Out-Null; Write-Host 'MultiClaude starts at sign-in (turn off in its menu)' }
if (-not $NoRouter) {
  & $cli router on --quiet | Out-Null
  # Windows only honors a claude:// default the person picks. claude-restyler's
  # Claude Router (ClaudeSplit.Link) reads the same hand-off files, so either does.
  $assoc = 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\claude'
  $def = (Get-ItemProperty "$assoc\UserChoiceLatest\ProgId" -ErrorAction SilentlyContinue).ProgId
  if (-not $def) { $def = (Get-ItemProperty "$assoc\UserChoice" -ErrorAction SilentlyContinue).ProgId }
  if ($def -ne 'MultiClaude.Link' -and $def -ne 'ClaudeSplit.Link') {
    Write-Host ''
    Write-Host 'One manual step, so signing in with Google works in every copy:'
    Write-Host '  in the Settings page that opens, set CLAUDE to MultiClaude.'
    Start-Process 'ms-settings:defaultapps?registeredAppUser=MultiClaude'
  }
}
& $cli list
if (-not $NoStart) { Start-Process (Join-Path $dest 'MultiClaude.exe'); Write-Host 'MultiClaude is in the notification area (click ^ if Windows hides it)' }
