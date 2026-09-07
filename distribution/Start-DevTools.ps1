param([string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'MarvijoDevTools'))
$ErrorActionPreference = 'Stop'
$currentPath = Join-Path $InstallRoot 'current.json'
$updater = Join-Path $PSScriptRoot 'Update-DevTools.ps1'
if (-not (Test-Path -LiteralPath $currentPath)) {
    & $updater -InstallRoot $InstallRoot -Force
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
Start-Process -FilePath $executable -WorkingDirectory $directory
$shell = (Get-Command pwsh -ErrorAction Stop).Source
Start-Process -FilePath $shell -ArgumentList @('-NoProfile','-File',('"' + $updater + '"'),'-InstallRoot',('"' + $InstallRoot + '"')) -WindowStyle Hidden
