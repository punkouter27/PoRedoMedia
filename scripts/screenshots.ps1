#requires -Version 7
<#
.SYNOPSIS
  Before/after screenshots of a UI change, as SCREENSHOTS/{YYMMDD}-{feature}.html and .png (AGENTS.md "UI previews").
.DESCRIPTION
  Captures the key screens LOCALLY (never the live site) at 1366x768 and 390x844, for the current working tree
  ("after") and for a git ref ("before", default HEAD~1) or a folder of PNGs you already have (-BeforeDir).
  Each side is the trimmed Release publish (what deploys), started like scripts/run-e2e.ps1 does: Test environment,
  mock AI, a throwaway Azurite container and the app on free ports, then removed again. The before ref is built from
  a temporary `git worktree`, which is removed at the end.
  Signed-in screens sign in through /dev-login (Test only) as screens@localhost.
  Writes:
    SCREENSHOTS/{YYMMDD}-{feature}.png   one side-by-side image: per screen, before | after at 1366 and at 390
    SCREENSHOTS/{YYMMDD}-{feature}.html  the same per screen, with -Notes as the "what changed" text
  Images are scaled down (JPEG inside the HTML, one PNG) so a page stays a few hundred KB.
.PARAMETER Feature
  Short name for the change, used in the file names (e.g. "one-layout").
.PARAMETER Before
  Git ref to capture as "before" (default HEAD~1). Ignored with -BeforeDir.
.PARAMETER BeforeDir
  Folder of before PNGs named <width>_<route>.png (route: "/" is "home", "/diag" is "diag", "/zz" is "zz"), e.g.
  1366_login.png, 390_diag.png. A screen with no file shows "no before".
.PARAMETER Routes
  Screens to capture. Prefix "in:" for a signed-in screen. Default: login, diag, signed-in Create and Library.
.PARAMETER Notes
  One line of "what changed" per screen, in -Routes order (optional).
.EXAMPLE
  pwsh scripts/screenshots.ps1 -Feature one-layout -Before 15d2983
.EXAMPLE
  pwsh scripts/screenshots.ps1 -Feature diag-cache -BeforeDir C:\temp\before -Routes /diag
#>
param(
    [Parameter(Mandatory)][string]$Feature,
    [string]$Before = 'HEAD~1',
    [string]$BeforeDir,
    [string[]]$Routes = @('/login', '/diag', 'in:/', 'in:/gallery'),
    [string[]]$Notes = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$sizes = @(@(1366, 768), @(390, 844))
$work = Join-Path ([IO.Path]::GetTempPath()) "PoRedoMedia-shots-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force $work | Out-Null

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop(); $port
}

function Get-Slug([string]$route) {
    $path = $route -replace '^in:', ''
    $slug = ($path.Trim('/') -replace '[^A-Za-z0-9]+', '-')
    if (-not $slug) { 'home' } else { $slug }
}

# Playwright: the E2EUI test project's copy (built here if missing), loaded into this PowerShell.
$playwrightDir = Join-Path $root 'tests/PoRedoMedia.E2EUI/bin/Release/net10.0'
if (-not (Test-Path (Join-Path $playwrightDir 'Microsoft.Playwright.dll'))) {
    dotnet build tests/PoRedoMedia.E2EUI -c Release -v q
    if ($LASTEXITCODE -ne 0) { throw 'Building tests/PoRedoMedia.E2EUI (for Playwright) failed.' }
}
Add-Type -Path (Join-Path $playwrightDir 'Microsoft.Playwright.dll')
Add-Type -AssemblyName System.Drawing

function Wait-Task($task) { $task.GetAwaiter().GetResult() }

# Publishes $source (a folder with the repo), starts it with a throwaway Azurite, captures every route, stops both.
function Invoke-Capture([string]$source, [string]$outDir) {
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $publish = Join-Path $work ("publish-" + (Split-Path $outDir -Leaf))
    dotnet publish (Join-Path $source 'src/PoRedoMedia.Api/PoRedoMedia.Api.csproj') -c Release -o $publish -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish of $source failed." }

    $azurite = "poredomedia-shots-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $blobPort = Get-FreePort; $tablePort = Get-FreePort
    docker run -d --rm --name $azurite -p "127.0.0.1:${blobPort}:10000" -p "127.0.0.1:${tablePort}:10002" `
        mcr.microsoft.com/azure-storage/azurite:3.37.0 azurite --blobHost 0.0.0.0 --tableHost 0.0.0.0 --loose --skipApiVersionCheck | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Azurite did not start. Is Docker running?' }
    $app = $null; $playwright = $null
    try {
        $devKey = 'Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=='
        $env:Storage__ConnectionString = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=$devKey;" +
            "BlobEndpoint=http://127.0.0.1:$blobPort/devstoreaccount1;TableEndpoint=http://127.0.0.1:$tablePort/devstoreaccount1;"
        $env:ASPNETCORE_ENVIRONMENT = 'Test'
        $env:Mocks__UseMockAi = 'true'
        foreach ($i in 1..60) {
            try { (New-Object System.Net.Sockets.TcpClient).Connect('127.0.0.1', $blobPort); break } catch { Start-Sleep -Milliseconds 500 }
        }
        $url = "http://localhost:$(Get-FreePort)"
        $app = Start-Process dotnet -PassThru -WindowStyle Hidden -WorkingDirectory $publish `
            -ArgumentList @((Join-Path $publish 'PoRedoMedia.Api.dll'), '--urls', $url)
        $up = $false
        foreach ($i in 1..120) {
            try { Invoke-WebRequest "$url/health/live" -TimeoutSec 5 | Out-Null; $up = $true; break } catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $up) { throw "The app did not start on $url." }

        $playwright = Wait-Task ([Microsoft.Playwright.Playwright]::CreateAsync())
        $browser = Wait-Task ($playwright.Chromium.LaunchAsync([Microsoft.Playwright.BrowserTypeLaunchOptions]@{ Headless = $true }))
        foreach ($size in $sizes) {
            $context = Wait-Task ($browser.NewContextAsync([Microsoft.Playwright.BrowserNewContextOptions]@{
                ViewportSize = [Microsoft.Playwright.ViewportSize]@{ Width = $size[0]; Height = $size[1] } }))
            $page = Wait-Task ($context.NewPageAsync())
            foreach ($route in $Routes) {
                $path = $route -replace '^in:', ''
                $target = if ($route.StartsWith('in:')) { "$url/dev-login?email=screens%40localhost&returnUrl=$([Uri]::EscapeDataString($path))" } else { "$url$path" }
                Wait-Task ($page.GotoAsync($target, [Microsoft.Playwright.PageGotoOptions]@{ WaitUntil = [Microsoft.Playwright.WaitUntilState]::NetworkIdle; Timeout = 120000 })) | Out-Null
                try { Wait-Task ($page.WaitForSelectorAsync('h1', [Microsoft.Playwright.PageWaitForSelectorOptions]@{ Timeout = 60000 })) | Out-Null } catch { }
                Wait-Task ($page.WaitForTimeoutAsync(1500)) | Out-Null
                $file = Join-Path $outDir ("{0}_{1}.png" -f $size[0], (Get-Slug $route))
                Wait-Task ($page.ScreenshotAsync([Microsoft.Playwright.PageScreenshotOptions]@{ Path = $file })) | Out-Null
                Write-Host "  $file"
            }
            Wait-Task ($context.CloseAsync()) | Out-Null
        }
        Wait-Task ($browser.CloseAsync()) | Out-Null
    }
    finally {
        if ($playwright) { $playwright.Dispose() }
        if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
        docker rm -f $azurite 2>$null | Out-Null
    }
}

$worktree = $null
try {
    # --- before -----------------------------------------------------------------------------------------------------
    if ($BeforeDir) {
        $beforeShots = (Resolve-Path $BeforeDir).Path
        $beforeLabel = "before ($(Split-Path $beforeShots -Leaf))"
    }
    else {
        $sha = (git rev-parse --short $Before).Trim()
        $worktree = Join-Path $work "before-$sha"
        git worktree add --detach $worktree $sha | Out-Null
        Write-Host "Before: $Before ($sha)"
        $beforeShots = Join-Path $work 'before'
        Invoke-Capture $worktree $beforeShots
        $beforeLabel = "before ($sha)"
    }

    # --- after ------------------------------------------------------------------------------------------------------
    Write-Host 'After: the working tree'
    $afterShots = Join-Path $work 'after'
    Invoke-Capture $root $afterShots
    $head = (git rev-parse --short HEAD).Trim()
    $dirty = if (git status --porcelain -- src) { ' + local changes' } else { '' }
    $afterLabel = "after ($head$dirty)"

    # --- the side-by-side PNG ---------------------------------------------------------------------------------------
    $scale = 0.35
    $gap = 12; $label = 22
    $cellW = [int](1366 * $scale); $cellH = [int](768 * $scale)
    $phoneW = [int](390 * $scale); $phoneH = [int](844 * $scale)
    $rowH = [Math]::Max($cellH, $phoneH) + $label + $gap
    $width = 2 * $cellW + 2 * $phoneW + 5 * $gap
    $height = 40 + $Routes.Count * $rowH
    $canvas = [System.Drawing.Bitmap]::new($width, $height)
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::FromArgb(244, 245, 249))
    $font = [System.Drawing.Font]::new('Segoe UI', 10, [System.Drawing.FontStyle]::Bold)
    $titleFont = [System.Drawing.Font]::new('Segoe UI', 13, [System.Drawing.FontStyle]::Bold)
    $ink = [System.Drawing.Brushes]::Black
    $red = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(192, 38, 45))
    $green = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(21, 128, 61))
    $g.DrawString("$Feature  ·  $beforeLabel → $afterLabel", $titleFont, $ink, $gap, 8)

    $htmlRows = [System.Text.StringBuilder]::new()
    function Get-Thumb([string]$file, [int]$w, [int]$h) {
        if (-not (Test-Path $file)) { return $null }
        $source = [System.Drawing.Image]::FromFile($file)
        try {
            $thumb = [System.Drawing.Bitmap]::new($w, $h)
            $tg = [System.Drawing.Graphics]::FromImage($thumb)
            $tg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $tg.Clear([System.Drawing.Color]::White)
            $tg.DrawImage($source, [System.Drawing.Rectangle]::new(0, 0, $w, [int]($source.Height * $w / $source.Width)))
            $tg.Dispose()
            $thumb
        }
        finally { $source.Dispose() }
    }
    function ConvertTo-DataUri($bitmap) {
        if (-not $bitmap) { return '' }
        $jpeg = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
        $parameters = [System.Drawing.Imaging.EncoderParameters]::new(1)
        $parameters.Param[0] = [System.Drawing.Imaging.EncoderParameter]::new([System.Drawing.Imaging.Encoder]::Quality, [long]70)
        $stream = [IO.MemoryStream]::new()
        $bitmap.Save($stream, $jpeg, $parameters)
        'data:image/jpeg;base64,' + [Convert]::ToBase64String($stream.ToArray())
    }

    for ($r = 0; $r -lt $Routes.Count; $r++) {
        $route = $Routes[$r]; $slug = Get-Slug $route
        $y = 40 + $r * $rowH
        $note = if ($r -lt $Notes.Count) { $Notes[$r] } else { '' }
        $cells = @(
            @{ Dir = $beforeShots; W = 1366; Tw = $cellW; Th = $cellH; Tag = 'before 1366'; Brush = $red },
            @{ Dir = $afterShots; W = 1366; Tw = $cellW; Th = $cellH; Tag = 'after 1366'; Brush = $green },
            @{ Dir = $beforeShots; W = 390; Tw = $phoneW; Th = $phoneH; Tag = 'before 390'; Brush = $red },
            @{ Dir = $afterShots; W = 390; Tw = $phoneW; Th = $phoneH; Tag = 'after 390'; Brush = $green })
        $x = $gap
        $uris = @()
        foreach ($cell in $cells) {
            $g.DrawString("$route  $($cell.Tag)", $font, $cell.Brush, $x, $y)
            $thumb = Get-Thumb (Join-Path $cell.Dir ("{0}_{1}.png" -f $cell.W, $slug)) $cell.Tw $cell.Th
            if ($thumb) { $g.DrawImage($thumb, $x, $y + $label); $uris += ConvertTo-DataUri $thumb; $thumb.Dispose() }
            else { $g.DrawString('no before', $font, $ink, $x + 8, $y + $label + 8); $uris += '' }
            $g.DrawRectangle([System.Drawing.Pens]::LightGray, $x, $y + $label, $cell.Tw, $cell.Th)
            $x += $cell.Tw + $gap
        }
        $img = { param($u, $w) if ($u) { "<img src=""$u"" width=""$w"" alt="""">" } else { '<p>no before</p>' } }
        [void]$htmlRows.Append(@"
<section><h2>$([System.Net.WebUtility]::HtmlEncode($route))</h2><p class="note">$([System.Net.WebUtility]::HtmlEncode($note))</p>
<div class="row"><figure><figcaption class="b">before · 1366</figcaption>$(& $img $uris[0] $cellW)</figure>
<figure><figcaption class="a">after · 1366</figcaption>$(& $img $uris[1] $cellW)</figure>
<figure><figcaption class="b">before · 390</figcaption>$(& $img $uris[2] $phoneW)</figure>
<figure><figcaption class="a">after · 390</figcaption>$(& $img $uris[3] $phoneW)</figure></div></section>
"@)
    }
    $g.Dispose()

    $out = Join-Path $root 'SCREENSHOTS'
    New-Item -ItemType Directory -Force $out | Out-Null
    $name = "$((Get-Date).ToUniversalTime().ToString('yyMMdd'))-$Feature"
    $png = Join-Path $out "$name.png"
    $canvas.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.Dispose()
    $title = [System.Net.WebUtility]::HtmlEncode($Feature)
    @"
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>$title</title>
<style>
  body { margin:0; padding:24px; background:#f4f5f9; font:14px/1.4 Inter, system-ui, sans-serif; color:#1c1f2a; }
  h1 { font-size:20px; margin:0 0 4px; } h2 { font-size:16px; margin:24px 0 4px; }
  .sub, .note { color:#5b6070; margin:0 0 8px; }
  .row { display:flex; gap:12px; flex-wrap:wrap; align-items:flex-start; }
  figure { margin:0; } figcaption { font-weight:700; margin:0 0 4px; }
  figcaption.b { color:#c0262d; } figcaption.a { color:#15803d; }
  img { display:block; border:1px solid #d9dce6; background:#fff; max-width:100%; height:auto; }
</style>
</head>
<body>
<h1>$title</h1>
<p class="sub">$([System.Net.WebUtility]::HtmlEncode("$beforeLabel → $afterLabel")) · local Release publish, Test environment, mock AI · 1366×768 and 390×844 · side by side: $name.png</p>
$($htmlRows.ToString())
</body>
</html>
"@ | Set-Content -Path (Join-Path $out "$name.html") -Encoding utf8NoBOM
    Write-Host "Wrote SCREENSHOTS/$name.html and SCREENSHOTS/$name.png ($([int]((Get-Item $png).Length / 1KB)) KB)"
}
finally {
    if ($worktree) { git worktree remove --force $worktree 2>$null | Out-Null; git worktree prune }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:Storage__ConnectionString, Env:ASPNETCORE_ENVIRONMENT, Env:Mocks__UseMockAi -ErrorAction SilentlyContinue
}
