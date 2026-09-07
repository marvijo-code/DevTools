param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'MarvijoDevTools'),
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
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
& (Join-Path $InstallRoot 'Update-DevTools.ps1') -InstallRoot $InstallRoot -Force
if ($LASTEXITCODE -ne 0) { throw 'The release could not be installed.' }
$shortcutDirectory = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $shortcutDirectory 'DevTools.lnk'))
$shortcut.TargetPath = $shell
$shortcut.Arguments = '-NoProfile -WindowStyle Hidden -File "' + (Join-Path $InstallRoot 'Start-DevTools.ps1') + '" -InstallRoot "' + $InstallRoot + '"'
$shortcut.WorkingDirectory = $InstallRoot
$shortcut.WindowStyle = 7
$current = Get-Content -LiteralPath (Join-Path $InstallRoot 'current.json') -Raw | ConvertFrom-Json
$shortcut.IconLocation = Join-Path $current.directory 'DevTools.exe'
$shortcut.Save()
if (-not $NoLaunch) { & (Join-Path $InstallRoot 'Start-DevTools.ps1') -InstallRoot $InstallRoot }
Write-Output ('Installed ' + $current.version + '. Updates are checked automatically when DevTools starts.')
