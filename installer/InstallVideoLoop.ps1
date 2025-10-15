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

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "VideoLoop\VideoLoop.csproj"
if (-not (Test-Path $projectPath)) {
    throw "Could not find VideoLoop.csproj at expected path: $projectPath"
}

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
