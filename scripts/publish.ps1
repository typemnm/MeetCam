$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $env:TEMP "meetcam-publish"
$sourceStage = Join-Path $stage "source"
$appStage = Join-Path $stage "app"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

& $dotnet publish (Join-Path $root "src\MeetCam.Source\MeetCam.Source.csproj") -c Release -r win-x64 --self-contained false -o $sourceStage
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet publish (Join-Path $root "src\MeetCam.App\MeetCam.App.csproj") -c Release -r win-x64 --self-contained false -o $appStage
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dest = "C:\Users\Public\MeetCam"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item (Join-Path $appStage "*") $dest -Recurse -Force
Copy-Item (Join-Path $sourceStage "MeetCam.Source.runtimeconfig.json") $dest -Force
Copy-Item (Join-Path $sourceStage "MeetCam.Source.deps.json") $dest -Force
Copy-Item (Join-Path $sourceStage "MeetCam.Source.comhost.dll") $dest -Force
$directShow = Join-Path $root "native\MeetCamDShow\MeetCamDShow.dll"
if (Test-Path $directShow) {
    Copy-Item $directShow $dest -Force
}
Write-Host "Published to $dest"

function Register-Dll($path) {
    if (-not (Test-Path $path)) { return 0 }
    $process = Start-Process -FilePath "$env:SystemRoot\System32\regsvr32.exe" -ArgumentList @("/s", $path) -Wait -PassThru -WindowStyle Hidden
    return $process.ExitCode
}
$mediaFoundation = Register-Dll (Join-Path $dest "MeetCam.Source.comhost.dll")
$directShowResult = Register-Dll (Join-Path $dest "MeetCamDShow.dll")
if ($mediaFoundation -ne 0 -or $directShowResult -ne 0) {
    Write-Host "Camera registration needs administrator approval. Start MeetCam.exe and allow the prompt."
    exit 0
}
Write-Host "Camera registered. Start $dest\MeetCam.exe"
