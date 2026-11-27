param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference = 'Stop'
$setup = (Resolve-Path -LiteralPath $Installer).Path
if ([IO.Path]::GetFileName($setup) -ne 'WarframeCompanion-win-x64-TestSetup.exe') {
    throw 'Use build-installer.ps1 -IsolatedTest: release installers share the real app registration.'
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('wfc-install-' + [Guid]::NewGuid().ToString('N'))
$destination = Join-Path $testRoot 'app'
$data = Join-Path $testRoot 'profile'
New-Item -ItemType Directory -Path $data -Force | Out-Null
$sentinel = Join-Path $data 'preserve-settings.txt'
'preserve-me' | Set-Content -LiteralPath $sentinel
$emptyRuntime = Join-Path $testRoot 'empty-dotnet'
$missingLog = Join-Path $testRoot 'missing-runtime.log'
New-Item -ItemType Directory -Path $emptyRuntime | Out-Null
$missing = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', '/NOCLOSEAPPLICATIONS', "/DIR=`"$destination`"", "/LOG=`"$missingLog`"") -Environment @{ DOTNET_ROOT_X64 = $emptyRuntime } -WindowStyle Hidden -Wait -PassThru
if ($missing.ExitCode -eq 0 -or (Test-Path -LiteralPath $destination)) { throw 'Setup continued with a missing runtime.' }
$missingText = Get-Content -Raw -LiteralPath $missingLog
if ($missingText -notmatch '\.NET Runtime 10 \(x64\)' -or $missingText -notmatch 'ASP\.NET Core Runtime 10 \(x64\)') {
    throw 'Setup did not identify the two missing .NET runtimes.'
}
Write-Output 'Setup identified missing .NET runtimes before installing any application files.'
function Install-Candidate {
    $install = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', '/NOCLOSEAPPLICATIONS', "/DIR=`"$destination`"") -WindowStyle Hidden -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "Installer failed: $($install.ExitCode)" }
}
Install-Candidate
$executable = Join-Path $destination 'WarframeCompanion.Desktop.exe'
$process = Start-Process -FilePath $executable -ArgumentList @('--headless','--offline','--no-game','--data-dir',"`"$data`"",'--port','28766') -WindowStyle Hidden -PassThru
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        try { $headers = @{ Authorization = (Get-Content -Raw (Join-Path $data 'api-28766.auth')).Trim().Substring(15) }; $state = Invoke-RestMethod 'http://127.0.0.1:28766/api/state' -Headers $headers -TimeoutSec 2; $ready = $true; break } catch { Start-Sleep -Milliseconds 300 }
    }
    if (!$ready -or $state.accounts.Count -ne 0) { throw 'Installed application did not start in isolated mode.' }
    Invoke-RestMethod 'http://127.0.0.1:28766/api/settings' -Headers $headers -Method Post -ContentType application/json -Body '{"notify":true}' | Out-Null
    Invoke-RestMethod 'http://127.0.0.1:28766/api/quit' -Headers $headers -Method Post -ContentType application/json -Body '{}' | Out-Null
    if (!$process.WaitForExit(15000)) { throw 'Installed application did not shut down.' }
} finally {
    if (!$process.HasExited) { Stop-Process -Id $process.Id -Force }
}
$retiredRuntime = Join-Path $destination 'System.Private.CoreLib.dll'
[IO.File]::WriteAllText($retiredRuntime, 'old bundled runtime fixture')
$retiredManaged = Join-Path $destination 'WarframeCompanion.Desktop.dll'
[IO.File]::WriteAllText($retiredManaged, 'old managed assembly fixture')
$retiredOcr = Join-Path $destination 'tools/tesseract/obsolete-test-library.dll'
New-Item -ItemType Directory -Path (Split-Path -Parent $retiredOcr) -Force | Out-Null
[IO.File]::WriteAllText($retiredOcr, 'old OCR dependency fixture')
$settingsHash = (Get-FileHash -LiteralPath (Join-Path $data 'state.sqlite')).Hash
Install-Candidate
if (Test-Path -LiteralPath $retiredRuntime) { throw 'Upgrade retained the old bundled runtime.' }
if (Test-Path -LiteralPath $retiredManaged) { throw 'Upgrade retained a managed assembly already inside the bundle.' }
if (Test-Path -LiteralPath $retiredOcr) { throw 'Upgrade retained an obsolete OCR dependency.' }
if ((Get-FileHash -LiteralPath (Join-Path $data 'state.sqlite')).Hash -ne $settingsHash) { throw 'Upgrade changed settings.' }
$uninstall = Start-Process -FilePath (Join-Path $destination 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
if ($uninstall.ExitCode -ne 0 -or (Test-Path -LiteralPath $executable)) { throw 'Uninstall failed.' }
if (!(Test-Path -LiteralPath $sentinel) -or (Get-FileHash -LiteralPath (Join-Path $data 'state.sqlite')).Hash -ne $settingsHash) { throw 'Uninstall changed user data.' }
Write-Output 'Install, app startup, upgrade, uninstall and user-data preservation passed.'
