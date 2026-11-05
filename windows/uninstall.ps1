# Removes the MultiClaude program, its Start menu entry, PATH entry, link
# router and sign-in start. Your Claude copies (~\Applications\Claude *), their
# profiles and ~\.config\multiclaude stay; remove copies from the menu first if
# you want them gone.
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\MultiClaude'
$cli = Join-Path $dest 'multiclaude.com'
if (Test-Path $cli) { & $cli router off --quiet | Out-Null }
Get-Process MultiClaude -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($dest, 'OrdinalIgnoreCase') } |
  ForEach-Object { Stop-Process -Id $_.Id -Force; $_.WaitForExit(5000) | Out-Null }
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name MultiClaude -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'MultiClaude.lnk') -ErrorAction SilentlyContinue
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath) {
  [Environment]::SetEnvironmentVariable('Path', ((($userPath -split ';') | Where-Object { $_ -and $_.TrimEnd('\') -ine $dest }) -join ';'), 'User')
}
foreach ($f in 'MultiClaude.exe', 'multiclaude.com') { Remove-Item (Join-Path $dest $f) -ErrorAction SilentlyContinue }
Remove-Item $dest -ErrorAction SilentlyContinue
Write-Host 'MultiClaude removed. Copies, profiles and ~\.config\multiclaude were kept.'
