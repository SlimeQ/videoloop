<# 
    Installs the VideoLoop application for the current user.
    - Publishes a self-contained build
    - Copies it to %LOCALAPPDATA%\VideoLoop
    - Creates a Start Menu shortcut
    - Adds a command-line shim named "videoloop"
#>

[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

function Write-Info($message) {
    Write-Host "[VideoLoop] $message"
}

function Get-ShortcutPath {
    param(
        [string]$ShortcutName
    )

    $startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
    if (-not (Test-Path $startMenu)) {
        New-Item -ItemType Directory -Path $startMenu | Out-Null
    }

    return Join-Path $startMenu $ShortcutName
}

function Ensure-DotNet {
    try {
        dotnet --version | Out-Null
    }
    catch {
        throw "The .NET SDK must be installed and available on PATH to run this installer."
    }
}

function New-FaviconFromIcon {
    param(
        [string]$IconPath,
        [int[]]$Sizes = @(16, 32, 48, 64, 128, 256)
    )

    Add-Type -AssemblyName System.Drawing

    if (-not (Test-Path $IconPath)) {
        throw "Cannot create favicon; icon not found at $IconPath"
    }

    $resolvedSizes = $Sizes | Sort-Object -Unique | Where-Object { $_ -gt 0 }
    if (-not $resolvedSizes) {
        throw "At least one positive size must be provided to generate a favicon."
    }

    $sourceImage = [System.Drawing.Image]::FromFile($IconPath)
    try {
        $entries = @()
        foreach ($size in $resolvedSizes) {
            $canvas = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $graphics = [System.Drawing.Graphics]::FromImage($canvas)
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.DrawImage($sourceImage, 0, 0, $size, $size)
            $graphics.Dispose()

            $pngStream = New-Object System.IO.MemoryStream
            $canvas.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
            $entries += [PSCustomObject]@{
                Size = $size
                Data = $pngStream.ToArray()
            }
            $pngStream.Dispose()
            $canvas.Dispose()
        }

        $iconStream = New-Object System.IO.MemoryStream
        $writer = New-Object System.IO.BinaryWriter($iconStream)
        try {
            $writer.Write([UInt16]0)   # Reserved
            $writer.Write([UInt16]1)   # Image type = icon
            $writer.Write([UInt16]$entries.Count)

            $offset = 6 + (16 * $entries.Count)
            foreach ($entry in $entries) {
                $size = $entry.Size
                $widthByte = if ($size -ge 256) { 0 } else { [byte]$size }
                $writer.Write([byte]$widthByte)
                $writer.Write([byte]$widthByte)
                $writer.Write([byte]0)  # Colors in palette
                $writer.Write([byte]0)  # Reserved
                $writer.Write([UInt16]0)
                $writer.Write([UInt16]32)
                $writer.Write([UInt32]$entry.Data.Length)
                $writer.Write([UInt32]$offset)
                $offset += $entry.Data.Length
            }

            foreach ($entry in $entries) {
                $writer.Write($entry.Data)
            }

            $writer.Flush()
            return ,$iconStream.ToArray()
        }
        finally {
            $writer.Dispose()
            $iconStream.Dispose()
        }
    }
    finally {
        $sourceImage.Dispose()
    }
}

function Get-ApplicationIconPath {
    param(
        [string]$ProjectPath
    )

    $projectDir = Split-Path -Parent $ProjectPath
    try {
        [xml]$projectXml = Get-Content -Path $ProjectPath
    }
    catch {
        throw "Failed to read csproj at $ProjectPath"
    }

    $iconRelative = $projectXml.Project.PropertyGroup |
        ForEach-Object { $_.ApplicationIcon } |
        Where-Object { $_ } |
        Select-Object -First 1

    if (-not $iconRelative) {
        throw "ApplicationIcon is not defined in $ProjectPath"
    }

    $iconPath = Join-Path $projectDir $iconRelative
    if (-not (Test-Path $iconPath)) {
        throw "Application icon not found at expected path: $iconPath"
    }

    return $iconPath
}

function Update-LibVlcFavicons {
    param(
        [string]$PublishDirectory,
        [byte[]]$FaviconBytes
    )

    $libVlcRoot = Join-Path $PublishDirectory "libvlc"
    if (-not (Test-Path $libVlcRoot)) {
        Write-Info "libvlc directory not found at $libVlcRoot. Skipping favicon update."
        return
    }

    if (-not $FaviconBytes -or $FaviconBytes.Length -eq 0) {
        throw "Favicon bytes were not generated."
    }

    $httpDirs = Get-ChildItem -Path $libVlcRoot -Directory -Recurse |
        Where-Object { $_.FullName -match "[\\/]lua[\\/]http$" }

    if (-not $httpDirs) {
        Write-Info "No libvlc lua\\http directories found. Skipping favicon update."
        return
    }

    foreach ($dir in $httpDirs) {
        $targetFavicon = Join-Path $dir.FullName "favicon.ico"
        [System.IO.File]::WriteAllBytes($targetFavicon, $FaviconBytes)
        Write-Info "Updated favicon at $targetFavicon"
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "VideoLoop\VideoLoop.csproj"
if (-not (Test-Path $projectPath)) {
    throw "Could not find VideoLoop.csproj at expected path: $projectPath"
}
$applicationIconPath = Get-ApplicationIconPath -ProjectPath $projectPath

Ensure-DotNet

$publishDir = Join-Path $PSScriptRoot "publish-temp"
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

$installRoot = Join-Path $env:LOCALAPPDATA "VideoLoop"
$exePath = Join-Path $installRoot "VideoLoop.exe"
$shortcutPath = Get-ShortcutPath -ShortcutName "Video Loop.lnk"
$cliShimDir = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps"
$cliShimPath = Join-Path $cliShimDir "videoloop.cmd"

Write-Info "Publishing application ($Configuration | $Runtime)..."
dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained `
    -p:PublishTrimmed=false `
    -o $publishDir | Out-Null

$faviconBytes = New-FaviconFromIcon -IconPath $applicationIconPath
Write-Info "Copying application icon to libvlc HTTP favicons..."
Update-LibVlcFavicons -PublishDirectory $publishDir -FaviconBytes $faviconBytes

if (Test-Path $installRoot) {
    if (-not $Force) {
        $response = Read-Host "Existing installation found. Overwrite? (y/N)"
        if ($response -notin @("y", "Y", "yes", "YES")) {
            Write-Info "Aborting installation."
            exit 1
        }
    }

    $running = Get-Process -Name "VideoLoop" -ErrorAction SilentlyContinue
    if ($running) {
        Write-Info "Stopping running instances of VideoLoop.exe"
        $running | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }

    Remove-Item -Recurse -Force $installRoot
}

New-Item -ItemType Directory -Path $installRoot | Out-Null
Write-Info "Copying files to $installRoot"
Copy-Item -Path (Join-Path $publishDir "*") -Destination $installRoot -Recurse

if (-not (Test-Path $cliShimDir)) {
    New-Item -ItemType Directory -Path $cliShimDir | Out-Null
}

$shimContent = @"
@echo off
"$exePath" %*
"@

Write-Info "Creating command-line shim at $cliShimPath"
Set-Content -Path $cliShimPath -Value $shimContent -Encoding ASCII

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $installRoot
$shortcut.IconLocation = "$exePath,0"
$shortcut.Description = "VideoLoop - loop videos from a folder"
$shortcut.Save()

Write-Info "Shortcut created at $shortcutPath"

Remove-Item -Recurse -Force $publishDir
Write-Info "Installation complete."
Write-Info "Launch from Start Menu: Video Loop"
Write-Info "Or run from command line: videoloop"
