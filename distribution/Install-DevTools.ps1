param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'MarvijoDevTools'),
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$shell = (Get-Command pwsh -ErrorAction Stop).Source
$null = Get-Command gh -ErrorAction Stop
New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
foreach ($script in @('Start-DevTools.ps1','Update-DevTools.ps1')) {
    $source = Join-Path $PSScriptRoot $script
    $destination = Join-Path $InstallRoot $script
    if ([IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($destination)) {
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
}
& $shell -NoProfile -File (Join-Path $InstallRoot 'Update-DevTools.ps1') -InstallRoot $InstallRoot -Force
if ($LASTEXITCODE -ne 0) { throw 'The release could not be installed.' }
$current = Get-Content -LiteralPath (Join-Path $InstallRoot 'current.json') -Raw | ConvertFrom-Json
& (Join-Path $current.directory 'Register-DevTools.ps1') -InstallRoot $InstallRoot
if (-not $NoLaunch) { Start-Process -FilePath (Join-Path $InstallRoot 'DevTools.Launcher.exe') }
Write-Output ('Installed ' + $current.version + '. Updates are checked automatically when DevTools starts.')
