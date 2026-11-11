param([Parameter(Mandatory)][string]$PackageDirectory, [switch]$IsolatedTest)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$payload = Join-Path $package 'framework-dependent'
if (!(Test-Path -LiteralPath (Join-Path $payload 'WarframeCompanion.Desktop.exe'))) { throw 'Runtime-dependent package is missing.' }
$compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$compilerPath = if ($compiler) { $compiler.Source } else {
    @((Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'),
      (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe')) |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$compilerPath -or !(Test-Path -LiteralPath $compilerPath)) { throw 'Inno Setup 6 or newer is required to build the installer.' }
# Remove only known shared-runtime files from older self-contained installs.
# Names come from the build machine's .NET 10 shared frameworks, never user profile data.
$runtimeNames = @('hostfxr.dll', 'hostpolicy.dll')
foreach ($line in (& dotnet --list-runtimes)) {
    if ($line -match '^(Microsoft.NETCore.App|Microsoft.AspNetCore.App) (10\.\S+) \[(.+)\]$') {
        $runtimeDirectory = Join-Path $Matches[3] $Matches[2]
        $runtimeNames += Get-ChildItem -LiteralPath $runtimeDirectory -File | Select-Object -ExpandProperty Name
    }
}
$cleanup = Join-Path $package 'runtime-cleanup.iss'
$runtimeNames | Sort-Object -Unique | Where-Object {
    $_ -match '^[A-Za-z0-9_.-]+$' -and !(Test-Path -LiteralPath (Join-Path $payload $_))
} | ForEach-Object { 'Type: files; Name: "{app}\' + $_ + '"' } | Set-Content -LiteralPath $cleanup -Encoding utf8NoBOM
$output = if ($IsolatedTest) { Join-Path $package 'installer-test' } else { $package }
[string[]]$defines = if ($IsolatedTest) { @('/DIsolatedTest') } else { @() }
& $compilerPath @defines "/DRuntimeCleanupInclude=$cleanup" "/DPayloadDirectory=$payload" "/DOutputDirectory=$output" (Join-Path $PSScriptRoot '../packaging/windows/WarframeCompanion.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Extension -in '.zip', '.exe' } | Sort-Object Name | ForEach-Object {
    ((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name)
} | Set-Content -LiteralPath (Join-Path $package 'SHA256SUMS-windows.txt') -Encoding utf8NoBOM
