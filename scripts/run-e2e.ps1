# scripts/run-e2e.ps1
# Runs the browser tests (tests/PoRedoMedia.E2EUI) against the trimmed Release publish, the same build that deploys:
# `dotnet publish -c Release` of the API (which carries the trimmed Blazor client), started from the publish folder in
# the Test environment with mock AI, no Key Vault, and storage on the local Azurite container.
# Azurite is started here if it is not running, and stopped again at the end if this script started it.
#
# ffmpeg (needed by the 5 video tests): the first of -FFmpegDir, $env:FFMPEG_DIR, ffmpeg on PATH, or the portable copy
# under %LOCALAPPDATA%\PoRedoMedia\ffmpeg. With -FetchFFmpeg and none of these, a portable build (gyan.dev "essentials")
# is downloaded there; it is never put in the repo and nothing is installed system-wide. The one used is printed.
#
# Usage: pwsh scripts/run-e2e.ps1 [-Filter <test name fragment>] [-FFmpegDir <folder with ffmpeg.exe>] [-FetchFFmpeg]
param([string]$Filter, [string]$FFmpegDir, [switch]$FetchFFmpeg)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$url = 'http://localhost:4100'
$publishDir = Join-Path ([IO.Path]::GetTempPath()) 'PoRedoMedia-e2e-publish'

# --- ffmpeg -------------------------------------------------------------------------------------------------------
$cacheDir = Join-Path $env:LOCALAPPDATA 'PoRedoMedia\ffmpeg'
function Find-CachedFFmpeg {
    if (Test-Path $cacheDir) {
        Get-ChildItem $cacheDir -Recurse -Filter 'ffmpeg.exe' -ErrorAction SilentlyContinue | Select-Object -First 1 |
            ForEach-Object { $_.DirectoryName }
    }
}
$ffDir = $null
if ($FFmpegDir) { $ffDir = $FFmpegDir }
elseif ($env:FFMPEG_DIR) { $ffDir = $env:FFMPEG_DIR }
elseif (Get-Command ffmpeg -ErrorAction SilentlyContinue) { $ffDir = Split-Path (Get-Command ffmpeg).Source -Parent }
else { $ffDir = Find-CachedFFmpeg }
if (-not $ffDir -and $FetchFFmpeg) {
    Write-Host "Fetching a portable ffmpeg into $cacheDir ..."
    New-Item -ItemType Directory -Force $cacheDir | Out-Null
    $zip = Join-Path $cacheDir 'ffmpeg-release-essentials.zip'
    Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $zip
    Expand-Archive $zip -DestinationPath $cacheDir -Force
    Remove-Item $zip
    $ffDir = Find-CachedFFmpeg
}
if ($ffDir -and (Test-Path (Join-Path $ffDir 'ffmpeg*'))) {
    $env:PATH = "$ffDir$([IO.Path]::PathSeparator)$env:PATH"
    $ffVersion = (& (Join-Path $ffDir 'ffmpeg') -hide_banner -version | Select-Object -First 1)
    Write-Host "ffmpeg: $ffDir ($ffVersion)"
}
else {
    Write-Warning 'ffmpeg: none found, so the 5 video tests will skip. Pass -FetchFFmpeg or -FFmpegDir <folder>.'
}

# --- Azurite ------------------------------------------------------------------------------------------------------
$azuriteWasRunning = [bool](docker ps -q --filter 'name=^poredomedia-azurite$' 2>$null)
docker compose up -d --wait azurite
if ($LASTEXITCODE -ne 0) { Write-Error 'Azurite did not start. Is Docker running?' }

$app = $null
$code = 1
try {
    # --- The Release publish (trimmed client), as the deploy makes it ---------------------------------------------
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish src/PoRedoMedia.Api/PoRedoMedia.Api.csproj -c Release -o $publishDir -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    dotnet build tests/PoRedoMedia.E2EUI -c Release -v q
    if ($LASTEXITCODE -ne 0) { throw 'Building the E2EUI tests failed.' }

    $env:ASPNETCORE_ENVIRONMENT = 'Test'
    $env:Mocks__UseMockAi = 'true'
    $env:Storage__ConnectionString = 'UseDevelopmentStorage=true'
    Write-Host "App: $publishDir\PoRedoMedia.Api.dll (Release publish, Test environment) on $url"
    $app = Start-Process dotnet -PassThru -WindowStyle Hidden -WorkingDirectory $publishDir `
        -ArgumentList @((Join-Path $publishDir 'PoRedoMedia.Api.dll'), '--urls', $url)
    $up = $false
    foreach ($i in 1..60) {
        try { Invoke-WebRequest "$url/health/live" -TimeoutSec 2 | Out-Null; $up = $true; break } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $up) { throw "The app did not start on $url." }

    $env:E2E_BASE_URL = $url
    # --filter must come before '--': everything after it is a runsettings value, so a filter there was ignored.
    $testArgs = @('test', 'tests/PoRedoMedia.E2EUI', '-c', 'Release', '--no-build')
    if ($Filter) { $testArgs += @('--filter', "FullyQualifiedName~$Filter") }
    $testArgs += @('--', 'xUnit.ParallelizeTestCollections=false')
    dotnet @testArgs
    $code = $LASTEXITCODE
}
finally {
    if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if (-not $azuriteWasRunning) { docker compose stop azurite | Out-Null }
}
exit $code
