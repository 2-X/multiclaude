# Builds MultiClaude.exe (tray) and multiclaude.com (CLI) into windows\build\
# with the C# compiler that ships with .NET Framework 4.x: nothing to install.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found: .NET Framework 4.x is required.' }

$out = Join-Path $here 'build'
New-Item -ItemType Directory -Force $out | Out-Null
$src = Get-ChildItem (Join-Path $here 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Management.dll',
        'System.Web.Extensions.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll' | ForEach-Object { "/r:$_" }
$common = @('/nologo', '/optimize+', '/platform:anycpu', '/utf8output', '/codepage:65001',
            "/resource:$(Join-Path $here 'pin-profile.js'),pin-profile.js") + $refs

& $csc @common /target:winexe "/out:$(Join-Path $out 'MultiClaude.exe')" @src
if ($LASTEXITCODE -ne 0) { throw "csc (tray) failed with exit code $LASTEXITCODE" }
& $csc @common /target:exe /define:CONSOLE "/out:$(Join-Path $out 'multiclaude.com')" @src
if ($LASTEXITCODE -ne 0) { throw "csc (cli) failed with exit code $LASTEXITCODE" }
Write-Host "Built $out\MultiClaude.exe and $out\multiclaude.com"
