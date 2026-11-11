param([string]$OutputDirectory = 'artifacts/ocr-minimal', [ValidateRange(1,32)][int]$Parallel = 4)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = Split-Path $PSScriptRoot -Parent
$work = Join-Path $repo 'artifacts/ocr-native-build'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory so old OCR DLLs cannot remain in the bundle.' }
New-Item -ItemType Directory -Force $work | Out-Null
$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (!$cmake) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $cmake = Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
}
function Fetch($Url, $File, $Hash) {
    if (!(Test-Path -LiteralPath $File)) { Invoke-WebRequest $Url -OutFile $File }
    if ((Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash -ne $Hash) { throw "Checksum mismatch: $File" }
}
function Cmake($Name, [string[]]$Arguments) {
    Write-Output $Name
    $log = Join-Path $work "$Name.log"
    & $cmake @Arguments *> $log
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $log -Tail 60; throw "$Name failed; see $log" }
}
$leptVersion = '1.87.0'; $tessVersion = '5.5.4'
Fetch "https://codeload.github.com/DanBloomberg/leptonica/tar.gz/refs/tags/$leptVersion" "$work/leptonica.tar.gz" 'FA2B40C5CAEA96D1BB93A97486262AED8731B69CE25A84A6BF5D25323E33F631'
Fetch "https://codeload.github.com/tesseract-ocr/tesseract/tar.gz/refs/tags/$tessVersion" "$work/tesseract.tar.gz" 'A8DBF8B8C8EE427FF6AD1D99116502FEAF59440AF1ADB893CAF60A6576826CF4'
foreach ($name in @('leptonica','tesseract')) {
    & tar -xf "$work/$name.tar.gz" -C $work
    if ($LASTEXITCODE -ne 0) { throw "Cannot extract $name" }
}
$prefix = "$work/install"
$common = @('-A','x64','-DBUILD_SHARED_LIBS=OFF','-DCMAKE_POLICY_VERSION_MINIMUM=3.5','-DCMAKE_POLICY_DEFAULT_CMP0091=NEW','-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded',"-DCMAKE_INSTALL_PREFIX=$prefix",'-DCMAKE_DISABLE_FIND_PACKAGE_PkgConfig=TRUE')
Cmake 'configure-leptonica' (@('-S',"$work/leptonica-$leptVersion",'-B',"$work/lept") + $common + @('-DSW_BUILD=OFF','-DBUILD_PROG=OFF','-DENABLE_ZLIB=OFF','-DENABLE_PNG=OFF','-DENABLE_GIF=OFF','-DENABLE_JPEG=OFF','-DENABLE_TIFF=OFF','-DENABLE_WEBP=OFF','-DENABLE_OPENJPEG=OFF'))
Cmake 'build-leptonica' @('--build',"$work/lept",'--config','Release','--target','INSTALL','--parallel',"$Parallel")
Cmake 'configure-tesseract' (@('-S',"$work/tesseract-$tessVersion",'-B',"$work/tess") + $common + @("-DCMAKE_PREFIX_PATH=$prefix",'-DWIN32_MT_BUILD=ON','-DDISABLED_LEGACY_ENGINE=ON','-DGRAPHICS_DISABLED=ON','-DDISABLE_CURL=ON','-DDISABLE_ARCHIVE=ON','-DDISABLE_TIFF=ON','-DOPENMP_BUILD=OFF','-DBUILD_TRAINING_TOOLS=ON','-DBUILD_TESTS=OFF','-DENABLE_NATIVE=OFF','-DENABLE_LTO=ON'))
Cmake 'build-tesseract' @('--build',"$work/tess",'--config','Release','--target','tesseract','combine_tessdata','--parallel',"$Parallel")
New-Item -ItemType Directory -Force "$output/tessdata", "$output/licenses", "$work/model" | Out-Null
Copy-Item -LiteralPath "$work/tess/bin/Release/tesseract.exe" -Destination $output
Fetch 'https://raw.githubusercontent.com/tesseract-ocr/tessdata/4.1.0/eng.traineddata' "$work/original.traineddata" 'DAA0C97D651C19FBA3B25E81317CD697E9908C8208090C94C3905381C23FC047'
$components = @('lstm','lstm-punc-dawg','lstm-word-dawg','lstm-number-dawg','lstm-unicharset','lstm-recoder','version') | ForEach-Object { "$work/model/eng.$_" }
$combine = "$work/tess/bin/Release/combine_tessdata.exe"
& $combine -e "$work/original.traineddata" @components
if ($LASTEXITCODE -ne 0) { throw 'Model extraction failed' }
& $combine "$work/model/eng."
if ($LASTEXITCODE -ne 0) { throw 'Model packing failed' }
if ((Get-FileHash "$work/model/eng.traineddata").Hash -ne '906538558589E563FAD743A10A7C4D4E041112C9D546B3F648AD42AD703E679F') { throw 'Model changed during packing' }
Copy-Item "$work/model/eng.traineddata" "$output/tessdata/eng.traineddata"
Copy-Item "$work/tesseract-$tessVersion/LICENSE" "$output/licenses/Tesseract.txt"
Copy-Item "$work/leptonica-$leptVersion/leptonica-license.txt" "$output/licenses/Leptonica.txt"
"Tesseract $tessVersion; Leptonica $leptVersion. Statically linked MSVC build. Original English LSTM model from tessdata 4.1.0. Source archives and model hashes are pinned in tools/build-ocr.ps1." | Set-Content "$output/licenses/BUILD.txt"
& "$output/tesseract.exe" --version
if ($LASTEXITCODE -ne 0) { throw 'Built OCR executable did not start' }
Write-Output $output
