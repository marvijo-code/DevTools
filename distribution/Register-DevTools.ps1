param([string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'MarvijoDevTools'))
$ErrorActionPreference = 'Stop'
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$current = Get-Content -LiteralPath (Join-Path $InstallRoot 'current.json') -Raw | ConvertFrom-Json
$launcher = Join-Path $InstallRoot 'DevTools.Launcher.exe'
foreach ($name in @('DevTools.Launcher.exe', 'Start-DevTools.ps1', 'Update-DevTools.ps1', 'Register-DevTools.ps1')) {
    $source = Join-Path $current.directory $name
    if (-not (Test-Path -LiteralPath $source)) { throw "Missing installation file: $name" }
    $target = Join-Path $InstallRoot $name
    if (-not (Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
        Copy-Item -LiteralPath $source -Destination $target -Force
    }
}
Copy-Item -LiteralPath (Join-Path $current.directory 'icon.ico') -Destination (Join-Path $InstallRoot 'DevTools.ico') -Force
(Get-Command pwsh -ErrorAction Stop).Source | Set-Content -LiteralPath (Join-Path $InstallRoot 'powershell-path.txt') -Encoding utf8NoBOM

function Set-DevToolsShortcut([string]$Path) {
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($Path)
    $shortcut.TargetPath = $launcher
    $shortcut.Arguments = ''
    $shortcut.Description = 'DevTools - developer tools and process manager'
    $shortcut.WorkingDirectory = $InstallRoot
    $shortcut.WindowStyle = 1
    $shortcut.IconLocation = (Join-Path $InstallRoot 'DevTools.ico') + ',0'
    $shortcut.Save()
    $register = Start-Process -FilePath $launcher -ArgumentList @('--register-shortcut', ('"' + $Path + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($register.ExitCode -ne 0) { throw 'The Windows shortcut identity could not be registered.' }
}
$programs = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'
New-Item -ItemType Directory -Path $programs -Force | Out-Null
Set-DevToolsShortcut (Join-Path $programs 'DevTools.lnk')
# Repair an existing DevTools pin, without adding pins or changing other applications.
$pin = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\DevTools.lnk'
if (Test-Path -LiteralPath $pin) {
    $old = (New-Object -ComObject WScript.Shell).CreateShortcut($pin)
    if ($old.TargetPath -like '*\MarvijoDevTools\*') { Set-DevToolsShortcut $pin }
}
