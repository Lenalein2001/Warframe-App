param([Parameter(Mandatory)][string]$PackageDirectory, [switch]$IsolatedTest)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$payload = Join-Path $package 'self-contained'
if (!(Test-Path -LiteralPath (Join-Path $payload 'WarframeCompanion.Desktop.exe'))) { throw 'Self-contained package is missing.' }
$compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$compilerPath = if ($compiler) { $compiler.Source } else {
    @((Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'),
      (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe')) |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$compilerPath -or !(Test-Path -LiteralPath $compilerPath)) { throw 'Inno Setup 6 or newer is required to build the installer.' }
$output = if ($IsolatedTest) { Join-Path $package 'installer-test' } else { $package }
[string[]]$defines = if ($IsolatedTest) { @('/DIsolatedTest') } else { @() }
& $compilerPath @defines "/DPayloadDirectory=$payload" "/DOutputDirectory=$output" (Join-Path $PSScriptRoot '../packaging/windows/WarframeCompanion.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Extension -in '.zip', '.exe' } | Sort-Object Name | ForEach-Object {
    ((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name)
} | Set-Content -LiteralPath (Join-Path $package 'SHA256SUMS-windows.txt') -Encoding utf8NoBOM
