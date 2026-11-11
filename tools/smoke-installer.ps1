param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference = 'Stop'
$setup = (Resolve-Path -LiteralPath $Installer).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('wfc-install-' + [Guid]::NewGuid().ToString('N'))
$destination = Join-Path $testRoot 'app'
$data = Join-Path $testRoot 'profile'
New-Item -ItemType Directory -Path $data -Force | Out-Null
$sentinel = Join-Path $data 'preserve-settings.txt'
'preserve-me' | Set-Content -LiteralPath $sentinel
function Install-Candidate {
    $install = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', "/DIR=`"$destination`"") -WindowStyle Hidden -Wait -PassThru
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
$settingsHash = (Get-FileHash -LiteralPath (Join-Path $data 'state.sqlite')).Hash
Install-Candidate
if ((Get-FileHash -LiteralPath (Join-Path $data 'state.sqlite')).Hash -ne $settingsHash) { throw 'Upgrade changed settings.' }
$uninstall = Start-Process -FilePath (Join-Path $destination 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
if ($uninstall.ExitCode -ne 0 -or (Test-Path -LiteralPath $executable)) { throw 'Uninstall failed.' }
if (!(Test-Path -LiteralPath $sentinel) -or (Get-FileHash -LiteralPath (Join-Path $data 'state.sqlite')).Hash -ne $settingsHash) { throw 'Uninstall changed user data.' }
Write-Output 'Install, app startup, upgrade, uninstall and user-data preservation passed.'
