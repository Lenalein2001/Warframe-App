param([Parameter(Mandatory)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$payload = Join-Path $package 'self-contained'
if (!(Test-Path -LiteralPath (Join-Path $payload 'WarframeCompanion.Desktop.exe'))) { throw 'Self-contained package is missing.' }
$compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$compilerPath = if ($compiler) { $compiler.Source } else { Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe' }
if (!(Test-Path -LiteralPath $compilerPath)) { throw 'Inno Setup 6 or newer is required to build the installer.' }
& $compilerPath "/DPayloadDirectory=$payload" "/DOutputDirectory=$package" (Join-Path $PSScriptRoot '../packaging/windows/WarframeCompanion.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
