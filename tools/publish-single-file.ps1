param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This single-file package targets Windows x64.' }
$repo = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo ('artifacts/single-file-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')) }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory.' }
& (Join-Path $PSScriptRoot 'build-native.ps1')
$payload = Join-Path $OutputDirectory 'portable'
& dotnet publish (Join-Path $repo 'src/Desktop/Desktop.csproj') -c Release -r win-x64 --self-contained false -p:SingleFilePortable=true -p:DebugType=None -p:DebugSymbols=false -p:RestoreLockedMode=true -o $payload
if ($LASTEXITCODE -ne 0) { throw 'Single-file publish failed.' }
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse)
if ($files.Count -ne 1 -or $files[0].Name -ne 'WarframeCompanion.Desktop.exe') { throw 'Single-file publish left unexpected sidecar files.' }
$executable = Join-Path $payload 'WarframeCompanion-win-x64.exe'
Move-Item -LiteralPath $files[0].FullName -Destination $executable
$commit = (& git -C $repo rev-parse HEAD).Trim()
$dirty = [bool](& git -C $repo status --porcelain --untracked-files=normal)
$result = [ordered]@{ commit=$commit; uncommittedChanges=$dirty; format='single-file'; runtime='win-x64'; requiresAspNetCore10=$true; ocr='optional'; bytes=(Get-Item -LiteralPath $executable).Length; sha256=(Get-FileHash -LiteralPath $executable).Hash }
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'package.json') -Encoding utf8
Write-Output ([IO.Path]::GetFullPath($executable))
