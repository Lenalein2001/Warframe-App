param(
    [ValidateSet('win-x64', 'linux-x64')][string]$Runtime = 'win-x64',
    [string]$TesseractRoot
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repoRoot 'src/Desktop/Desktop.csproj'
if ($Runtime.StartsWith('win-')) {
    if (!$IsWindows) { throw 'Build Windows desktop packages on Windows.' }
    if (!$TesseractRoot -or !(Test-Path -LiteralPath (Join-Path $TesseractRoot 'tesseract.exe')) -or !(Test-Path -LiteralPath (Join-Path $TesseractRoot 'tessdata/eng.traineddata')) -or !(Test-Path -LiteralPath (Join-Path $TesseractRoot 'licenses'))) { throw 'Supply -TesseractRoot with the complete OCR bundle, English data and licenses.' }
    & (Join-Path $PSScriptRoot 'build-native.ps1')
} else {
    if (!$IsLinux) { throw 'Build Linux desktop packages on Linux so native libraries match the target.' }
    & cmake -S (Join-Path $repoRoot 'src/Native') -B (Join-Path $repoRoot 'src/Native/build') -DCMAKE_BUILD_TYPE=Release
    if ($LASTEXITCODE -ne 0) { throw 'Native configure failed.' }
    & cmake --build (Join-Path $repoRoot 'src/Native/build') --parallel
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
}
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$outputRoot = Join-Path $repoRoot "artifacts/companion-$Runtime-$stamp"
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$measurements = @()
foreach ($selfContained in @($false, $true)) {
    $edition = if ($selfContained) { 'self-contained' } else { 'framework-dependent' }
    $destination = Join-Path $outputRoot $edition
    $arguments = @('publish', $project, '-c', 'Release', '-r', $Runtime,
        '--self-contained', $selfContained.ToString().ToLowerInvariant(), '-o', $destination,
        '-p:DebugType=None', '-p:DebugSymbols=false', '-p:RestoreLockedMode=true')
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Publishing $edition failed." }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/USAGE.md') -Destination (Join-Path $destination 'README.md')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $destination
    Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses') -Destination $destination -Recurse
    if ($Runtime.StartsWith('win-')) {
        New-Item -ItemType Directory -Path (Join-Path $destination 'tools') | Out-Null
        Copy-Item -LiteralPath $TesseractRoot -Destination (Join-Path $destination 'tools/tesseract') -Recurse
    } else {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging/linux/Portable.sh') -Destination $destination
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'web/fonts/OFL-Rajdhani.txt'), (Join-Path $repoRoot 'web/fonts/OFL-SourceSans3.txt') -Destination (Join-Path $destination 'licenses')
    $portable = Join-Path $outputRoot "$edition-portable"
    New-Item -ItemType Directory -Path $portable | Out-Null
    Copy-Item -LiteralPath $destination -Destination (Join-Path $portable 'app') -Recurse
    $innerLauncher = Join-Path $portable ('app/' + $(if ($Runtime.StartsWith('win-')) { 'Portable.cmd' } else { 'Portable.sh' }))
    if (Test-Path -LiteralPath $innerLauncher) { Remove-Item -LiteralPath $innerLauncher }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging/PORTABLE.md') -Destination (Join-Path $portable 'README.md')
    if ($Runtime.StartsWith('win-')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'src/Native/build/Release/companion_launcher.exe') -Destination (Join-Path $portable 'Start Warframe Companion.exe')
    } else {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging/linux/Portable.sh') -Destination $portable
    }
    if ($Runtime.StartsWith('win-')) {
        $archive = "$destination.zip"
        Compress-Archive -Path "$portable/*" -DestinationPath $archive
    } else {
        $archive = "$destination.tar.gz"
        & python (Join-Path $PSScriptRoot 'archive-companion.py') $portable $archive
        if ($LASTEXITCODE -ne 0) { throw 'Archive creation failed.' }
    }
    $files = @(Get-ChildItem -LiteralPath $destination -Recurse -File)
    $measurements += [PSCustomObject]@{
        edition = $edition
        runtime = $Runtime
        requiresAspNetCore10 = -not $selfContained
        fileCount = $files.Count
        installedBytes = ($files | Measure-Object -Property Length -Sum).Sum
        archiveBytes = (Get-Item -LiteralPath $archive).Length
        sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        scope = 'Desktop C# replacement candidate; see validation report for outstanding live-platform checks'
    }
}
$measurements | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'measurements.json')
$measurements | Format-Table
Write-Output $outputRoot
