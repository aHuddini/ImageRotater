# Builds the ImageRotater branding kit: PNG rasters of the SVGs in assets/.
# Rasterises with headless Edge (the WebView2 runtime is already a project
# dependency), so no ImageMagick or Inkscape install is required.
#
#   assets/imagerotater-mark.svg  -> icon.png (256)  and assets/mark-512.png
#   assets/banner.svg             -> assets/banner.png (1200x320)
#   assets/addon-header.svg       -> assets/addon-header.png (1280x720)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "assets"

$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "${env:ProgramFiles}\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $edge) { throw "Microsoft Edge not found - required to rasterise SVG." }

function Export-Png {
    param([string]$SvgPath, [string]$PngPath, [int]$Width, [int]$Height, [int]$Scale = 1)

    $uri = ([System.Uri]$SvgPath).AbsoluteUri
    $tmp = Join-Path $env:TEMP ("irshot_" + [System.Guid]::NewGuid().ToString("N"))

    # Edge reports success on stderr; PS 5.1 turns that into a NativeCommandError,
    # so run it through cmd and discard both streams.
    # The window is the SVG's own size; the device scale factor multiplies the
    # output, which is how one 256px mark also yields the 512px raster.
    $args = "--headless --disable-gpu --hide-scrollbars --force-device-scale-factor=$Scale " +
            "--default-background-color=00000000 " +
            "--screenshot=`"$PngPath`" --window-size=$Width,$Height " +
            "--user-data-dir=`"$tmp`" `"$uri`""
    cmd /c "`"$edge`" $args >nul 2>&1"

    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path $PngPath)) { throw "Failed to rasterise $SvgPath" }
    Write-Host ("  {0} ({1}x{2}, {3:N0} bytes)" -f (Split-Path $PngPath -Leaf), ($Width * $Scale), ($Height * $Scale), (Get-Item $PngPath).Length)
}

Write-Host "Rasterising branding..."
Export-Png (Join-Path $assets "imagerotater-mark.svg") (Join-Path $root "icon.png") 256 256
Export-Png (Join-Path $assets "imagerotater-mark.svg") (Join-Path $assets "mark-512.png") 256 256 2
Export-Png (Join-Path $assets "banner.svg") (Join-Path $assets "banner.png") 1200 320
Export-Png (Join-Path $assets "addon-header.svg") (Join-Path $assets "addon-header.png") 1280 720
Write-Host "Done."
