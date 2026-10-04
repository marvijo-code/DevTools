param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'MarvijoDevTools'),
    [switch]$NoElevate,
    [switch]$Elevate
)
$ErrorActionPreference = 'Stop'

<#
    DevTools starts as administrator by DEFAULT, because the fast NTFS Master File
    Table folder scan needs the raw-volume handle. The app stores the user's choice
    in its own settings.json, so a missing or unreadable file, or a missing
    property, keeps the administrator default.

    -NoElevate (the "DevTools --no-admin" shortcut) and -Elevate override the stored
    preference for one launch.
#>
function Get-DevToolsRunAsAdministrator {
    param(
        [switch]$NoElevate,
        [switch]$Elevate,
        [string]$SettingsDirectory = (Join-Path $env:LOCALAPPDATA 'DevTools')
    )

    if ($Elevate) { return $true }
    if ($NoElevate) { return $false }

    $settingsPath = Join-Path $SettingsDirectory 'settings.json'
    if (-not (Test-Path -LiteralPath $settingsPath)) { return $true }

    try {
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        if ($null -eq $settings.RunAsAdministrator) { return $true }
        return [bool]$settings.RunAsAdministrator
    }
    catch { return $true }
}

# Dot-sourcing this file loads the function above without starting anything, so
# Test-DevToolsLaunchPreference.ps1 can exercise the decision.
if ($MyInvocation.InvocationName -eq '.') { return }

$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$currentPath = Join-Path $InstallRoot 'current.json'
$updater = Join-Path $PSScriptRoot 'Update-DevTools.ps1'
$shell = (Get-Command pwsh -ErrorAction Stop).Source
if (-not (Test-Path -LiteralPath $currentPath)) {
    & $shell -NoProfile -File $updater -InstallRoot $InstallRoot -Force
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $currentPath)) { throw 'DevTools installation failed.' }
}
$current = Get-Content -LiteralPath $currentPath -Raw | ConvertFrom-Json
$directory = [IO.Path]::GetFullPath($current.directory)
$boundary = [IO.Path]::GetFullPath((Join-Path $InstallRoot 'versions')) + [IO.Path]::DirectorySeparatorChar
if (-not $directory.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'The installed app path is invalid.' }
$executable = Join-Path $directory 'DevTools.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'The installed app executable is missing.' }
$versionUpdater = Join-Path $directory 'Update-DevTools.ps1'
if (Test-Path -LiteralPath $versionUpdater) { $updater = $versionUpdater }

if (Get-DevToolsRunAsAdministrator -NoElevate:$NoElevate -Elevate:$Elevate) {
    try {
        Start-Process -FilePath $executable -WorkingDirectory $directory -Verb RunAs
    }
    catch {
        Write-Error 'DevTools starts with administrator rights. Approve the Windows prompt, or open "DevTools (no admin)" from the Start menu instead.'
        exit 2
    }
}
else {
    Start-Process -FilePath $executable -WorkingDirectory $directory
}
Start-Process -FilePath $shell -ArgumentList @('-NoProfile','-File',('"' + $updater + '"'),'-InstallRoot',('"' + $InstallRoot + '"')) -WindowStyle Hidden
