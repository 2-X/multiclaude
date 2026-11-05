# Builds dist\MultiClaude-Windows-<version>.zip, the download for Windows:
# the two binaries, the installer, the uninstaller and READ ME FIRST.txt in one
# folder. The version comes from Engine.VERSION; READ ME FIRST.txt must match.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
& (Join-Path $here 'build.ps1')

$ver = ([regex]::Match((Get-Content (Join-Path $here 'src\Engine.cs') -Raw), 'VERSION = "([^"]+)"')).Groups[1].Value
if (-not $ver) { throw 'no VERSION in src\Engine.cs' }
$readme = Join-Path $here 'READ ME FIRST.txt'
if ((Get-Content $readme -TotalCount 1) -notmatch [regex]::Escape("version $ver")) { throw "READ ME FIRST.txt doesn't say version $ver" }
$built = (Get-Item (Join-Path $here 'build\MultiClaude.exe')).VersionInfo.ProductVersion
if ($built -ne $ver) { throw "MultiClaude.exe says $built, Engine.VERSION says $ver (update Program.cs's assembly attributes)" }

$name = "MultiClaude-Windows-$ver"
$stage = Join-Path $root "dist\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $here 'build\MultiClaude.exe'), (Join-Path $here 'build\multiclaude.com'), (Join-Path $here 'install.ps1'),
  (Join-Path $here 'uninstall.ps1'), $readme $stage
Copy-Item (Join-Path $here 'install.cmd') (Join-Path $stage 'Install MultiClaude.cmd')
Copy-Item (Join-Path $here 'uninstall.cmd') (Join-Path $stage 'Uninstall MultiClaude.cmd')
$zip = Join-Path $root "dist\$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, 'Optimal', $true)
Remove-Item $stage -Recurse -Force
$h = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
Write-Host "Built $zip ($([math]::Round((Get-Item $zip).Length / 1KB)) KB)"
Write-Host "sha256 $h"
