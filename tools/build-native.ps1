param([ValidateSet('Release','Debug')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '../companion/Native'
$build = Join-Path $source 'build'
$cmake = Get-Command cmake -ErrorAction SilentlyContinue
if ($cmake) { $cmakePath = $cmake.Source } else {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $cmakePath = Join-Path $installation 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
}
if (!(Test-Path -LiteralPath $cmakePath)) { throw 'CMake and a C++20 compiler are required.' }
& $cmakePath -S $source -B $build -A x64
if ($LASTEXITCODE -ne 0) { throw 'Native configure failed.' }
& $cmakePath --build $build --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
& (Join-Path (Split-Path $cmakePath) 'ctest.exe') --test-dir $build -C $Configuration --output-on-failure
if ($LASTEXITCODE -ne 0) { throw 'Native tests failed.' }
