#!/usr/bin/env pwsh
# scripts/ci-package.ps1 -PublishDir <dir>
# Adds what App Service needs beside the published app: a static Linux ffmpeg/ffprobe in
# <PublishDir>/ffmpeg (FFmpegProcess looks for exactly that folder next to the app assembly) and
# fonts/DejaVuSans-Bold.ttf (FFmpegProcess.BundledFontPath), then encodes one captioned frame with
# both so a bad archive fails here rather than on the first render in production.
#
# The shared deploy.yml runs it after `dotnet publish`; scripts/package.sh runs it for a hand-made
# zip. Linux only (the binaries and the font are Linux ones): use WSL on Windows.
#
# The upload/zip step may drop the execute bit; FFmpegProcess.EnsureExecutable restores it at
# startup, which is why the site must not use WEBSITE_RUN_FROM_PACKAGE (read-only wwwroot).
param([Parameter(Mandatory)][string]$PublishDir)

$ErrorActionPreference = 'Stop'
if (-not $IsLinux) { throw 'ci-package.ps1 bundles Linux ffmpeg and fonts; run it on Linux or WSL.' }
if (-not (Test-Path $PublishDir)) { throw "Publish folder not found: $PublishDir" }
$PublishDir = (Resolve-Path $PublishDir).Path

# Statically linked ffmpeg, bundled so the app ships as code on the F1 plan rather than as a container.
# Must be the **gpl** variant: the render pipeline encodes with libx264, which the lgpl builds omit.
# Must be the non-`shared` archive: only the static build's binaries can be lifted out on their own.
# `latest` + `n8.1-latest` are BtbN's rolling names and the only ones safe to reference: dated
# `autobuild-*` tags are pruned after about two weeks. A checksum pin is therefore impossible; the
# verification below is behavioural instead.
$tag = if ($env:FFMPEG_TAG) { $env:FFMPEG_TAG } else { 'latest' }
$archive = if ($env:FFMPEG_ARCHIVE) { $env:FFMPEG_ARCHIVE } else { 'ffmpeg-n8.1-latest-linux64-gpl-8.1' }
$font = '/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf'

$work = Join-Path ([IO.Path]::GetTempPath()) "ci-package-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $tarball = Join-Path $work 'ffmpeg.tar.xz'
    curl -fsSL -o $tarball "https://github.com/BtbN/FFmpeg-Builds/releases/download/$tag/$archive.tar.xz"
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg download failed ($tag/$archive)" }
    # Recorded in the log so a bad build can be identified afterwards.
    "sha256 $((Get-FileHash $tarball -Algorithm SHA256).Hash.ToLowerInvariant())  ffmpeg.tar.xz"

    $bin = Join-Path $PublishDir 'ffmpeg'
    New-Item -ItemType Directory -Force $bin | Out-Null
    tar -xJf $tarball --strip-components=2 -C $bin "$archive/bin/ffmpeg" "$archive/bin/ffprobe"
    if ($LASTEXITCODE -ne 0) { throw 'Extracting ffmpeg/ffprobe failed' }
    chmod +x (Join-Path $bin 'ffmpeg') (Join-Path $bin 'ffprobe')
    if ($LASTEXITCODE -ne 0) { throw 'chmod failed' }
    $ffmpeg = Join-Path $bin 'ffmpeg'
    $ffprobe = Join-Path $bin 'ffprobe'

    (& $ffmpeg -version)[0]
    if ($LASTEXITCODE -ne 0) { throw 'bundled ffmpeg does not run' }
    (& $ffprobe -version)[0]
    if ($LASTEXITCODE -ne 0) { throw 'bundled ffprobe does not run' }
    if (-not ((& $ffmpeg -hide_banner -encoders) -match 'libx264')) {
        throw 'bundled ffmpeg lacks libx264 (wrong build variant)'
    }

    # The App Service .NET image is not known to carry any font, and both the meme renderer and
    # ffmpeg's drawtext need one.
    if (-not (Test-Path $font)) {
        sudo apt-get update
        sudo apt-get install -y --no-install-recommends fonts-dejavu-core
        if ($LASTEXITCODE -ne 0) { throw 'Installing fonts-dejavu-core failed' }
    }
    $fonts = Join-Path $PublishDir 'fonts'
    New-Item -ItemType Directory -Force $fonts | Out-Null
    Copy-Item $font $fonts -Force
    $bundledFont = Join-Path $fonts 'DejaVuSans-Bold.ttf'

    # An encoder listed is not an encoder that works, and a font present is not a font drawtext can
    # open. Encode one captioned frame with the bundled binaries and the bundled font.
    $smoke = Join-Path $work 'smoke.mp4'
    & $ffmpeg -hide_banner -loglevel error -f lavfi -i 'testsrc=size=64x64:rate=1' `
        -vf "drawtext=text='OK':fontfile='$bundledFont':fontsize=12" -frames:v 1 -c:v libx264 -y $smoke
    if ($LASTEXITCODE -ne 0) { throw 'ffmpeg smoke encode failed' }
    $codec = & $ffprobe -v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 $smoke
    if ($LASTEXITCODE -ne 0 -or "$codec".Trim() -ne 'h264') { throw "ffprobe did not read back an h264 stream (got '$codec')" }
    du -sh $PublishDir
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
