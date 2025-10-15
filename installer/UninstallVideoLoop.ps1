<# 
    Removes the VideoLoop application that was installed by InstallVideoLoop.ps1.
    Deletes application files, Start Menu shortcut, and CLI shim.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

function Write-Info($message) {
    Write-Host "[VideoLoop] $message"
}

function Get-ShortcutPath {
    $startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
    return Join-Path $startMenu "Video Loop.lnk"
}

$installRoot = Join-Path $env:LOCALAPPDATA "VideoLoop"
$shortcutPath = Get-ShortcutPath
$cliShimPath = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\videoloop.cmd"

if (Test-Path $installRoot) {
    Write-Info "Removing $installRoot"
    Remove-Item -Recurse -Force $installRoot
}

if (Test-Path $shortcutPath) {
    Write-Info "Removing Start Menu shortcut"
    Remove-Item -Force $shortcutPath
}

if (Test-Path $cliShimPath) {
    Write-Info "Removing command-line shim"
    Remove-Item -Force $cliShimPath
}

Write-Info "VideoLoop uninstall complete."
