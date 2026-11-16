<#
.SYNOPSIS
    Installs LocalVault for the current user (no admin rights needed).

.DESCRIPTION
    Copies .\publish\LocalVault.exe to %LocalAppData%\Programs\LocalVault\ and creates a
    Start Menu shortcut. Your vault data (%LocalAppData%\LocalVault\) is never touched.

.PARAMETER Desktop
    Also create a desktop shortcut.

.PARAMETER StartWithWindows
    Register LocalVault to start minimized to the tray when you sign in.

.PARAMETER Launch
    Start LocalVault after installing.

.PARAMETER Uninstall
    Remove the installed exe, shortcuts and startup entry (keeps your vault data).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\CreateShortcut.ps1 -Launch
#>
[CmdletBinding()]
param(
    [switch]$Desktop,
    [switch]$StartWithWindows,
    [switch]$Launch,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$source      = Join-Path $PSScriptRoot 'publish\LocalVault.exe'
$installDir  = Join-Path $env:LOCALAPPDATA 'Programs\LocalVault'
$installExe  = Join-Path $installDir 'LocalVault.exe'
$startMenu   = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$startLink   = Join-Path $startMenu 'LocalVault.lnk'
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'LocalVault.lnk'
$runKey      = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Stop-LocalVault {
    $running = Get-Process -Name 'LocalVault' -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host 'Closing running LocalVault...'
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 800
    }
}

function New-Shortcut([string]$path, [string]$target) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $target
    $lnk.WorkingDirectory = Split-Path $target
    $lnk.IconLocation = "$target,0"
    $lnk.Description = 'LocalVault - local password manager'
    $lnk.Save()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
}

if ($Uninstall) {
    Stop-LocalVault
    foreach ($p in $startLink, $desktopLink) { if (Test-Path $p) { Remove-Item $p -Force } }
    if (Get-ItemProperty -Path $runKey -Name 'LocalVault' -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $runKey -Name 'LocalVault'
    }
    if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
    Write-Host 'LocalVault uninstalled. Your vault data in %LocalAppData%\LocalVault was kept.' -ForegroundColor Green
    return
}

if (-not (Test-Path $source)) {
    throw "Could not find $source. Build it first with:`n  dotnet publish src/LocalVault/LocalVault.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish"
}

Stop-LocalVault
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item $source $installExe -Force
Unblock-File $installExe -ErrorAction SilentlyContinue
Write-Host "Installed: $installExe"

New-Shortcut $startLink $installExe
Write-Host "Start Menu shortcut: $startLink"

if ($Desktop) {
    New-Shortcut $desktopLink $installExe
    Write-Host "Desktop shortcut: $desktopLink"
}

$existingRun = (Get-ItemProperty -Path $runKey -Name 'LocalVault' -ErrorAction SilentlyContinue).LocalVault
if ($StartWithWindows -or $existingRun) {
    # Also repoints an existing entry (e.g. one created from a dev build) at the installed exe.
    Set-ItemProperty -Path $runKey -Name 'LocalVault' -Value "`"$installExe`" --tray"
    Write-Host 'Start with Windows: enabled (starts minimized to the tray)'
}

Write-Host ''
Write-Host 'Done. Launch LocalVault from the Start Menu, or run:' -ForegroundColor Green
Write-Host "  & `"$installExe`""

if ($Launch) { Start-Process $installExe }
