param([Parameter(Mandatory)][string]$LauncherPath)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('DevTools launch verification ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$launcher = Join-Path $root 'DevTools.Launcher.exe'
Copy-Item -LiteralPath $LauncherPath -Destination $launcher
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-DevTools.ps1') -Destination $root
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$shellPath = Join-Path $root 'powershell-path.txt'
$updateProof = Join-Path $root 'updated.txt'
# These test processes only write proof of the selected version. They have no UI.
$source = Join-Path $root 'LaunchProbe.cs'
@'
using System;
using System.IO;
class LaunchProbe {
    static void Main() {
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launched.txt"), "started");
    }
}
'@ | Set-Content -LiteralPath $source
$sdk = dotnet --list-sdks | Select-Object -Last 1
if ($sdk -notmatch '^(\S+) \[(.+)\]$') { throw 'Could not locate the Roslyn compiler.' }
$compiler = Join-Path $Matches[2] ($Matches[1] + '\Roslyn\bincore\csc.dll')
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$probe = Join-Path $root 'LaunchProbe.exe'
dotnet $compiler /nologo /noconfig /nostdlib+ /target:winexe ("/reference:$framework\mscorlib.dll") ("/out:$probe") $source
if ($LASTEXITCODE -ne 0) { throw 'Launch probe compilation failed.' }
$initialHash = (Get-FileHash -LiteralPath $launcher).Hash

function Invoke-HeadlessLaunch([string]$Directory) {
    # The fixture executable has no windows, and --no-admin suppresses elevation.
    $child = Start-Process -FilePath $launcher -ArgumentList '--no-admin' -WindowStyle Hidden -PassThru
    try {
        if (-not $child.WaitForExit(30000) -or $child.ExitCode -ne 0) { throw "Launcher failed for $Directory" }
    }
    finally {
        $child.Dispose()
    }

    $proof = Join-Path $Directory 'launched.txt'
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $proof) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $proof)) { throw "Wrong launch target for $Directory" }
    if ((Get-FileHash -LiteralPath $launcher).Hash -ne $initialHash) { throw 'Launcher changed during version transition.' }
}

function Confirm-UpdateVersion([string]$Directory) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $updateProof) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $updateProof) -or [IO.File]::ReadAllText($updateProof) -ne $Directory) {
        throw 'The launcher started the wrong version updater.'
    }
}

foreach ($version in @('v1.0.0', 'v1.0.1')) {
    $directory = Join-Path $root ('versions\' + $version)
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Copy-Item -LiteralPath $probe -Destination (Join-Path $directory 'DevTools.exe')
    @"
param([string]`$InstallRoot)
`$target = Join-Path `$InstallRoot 'updated.txt'
[IO.File]::WriteAllText(`$target + '.tmp', `$PSScriptRoot)
[IO.File]::Move(`$target + '.tmp', `$target)
"@ | Set-Content -LiteralPath (Join-Path $directory 'Update-DevTools.ps1')
    @{version=$version; directory=$directory} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'current.json')
    $updateDue = $version -eq 'v1.0.1'
    $checkedAt = [DateTimeOffset]::UtcNow
    if ($updateDue) {
        $checkedAt = $checkedAt.AddHours(-7)
        $pwsh | Set-Content -LiteralPath $shellPath -Encoding utf8NoBOM
    }
    @{checkedAt=$checkedAt.ToString('o')} | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $root 'update-state.json')
    Invoke-HeadlessLaunch $directory
    if ($updateDue) { Confirm-UpdateVersion $directory }
    Write-Output "PASS: permanent launcher started $version"
}
# Simulate a missing installation pointer without making any network requests.
$bootstrapDirectory = Join-Path $root 'versions\v1.0.1'
[IO.File]::Delete((Join-Path $root 'current.json'))
[IO.File]::Delete((Join-Path $bootstrapDirectory 'launched.txt'))
[IO.File]::Delete($updateProof)
@"
param([string]`$InstallRoot, [switch]`$Force)
if (-not `$Force) { exit 1 }
@{version='v1.0.1'; directory=(Join-Path `$InstallRoot 'versions\v1.0.1')} | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path `$InstallRoot 'current.json')
[IO.File]::WriteAllText((Join-Path `$InstallRoot 'bootstrapped.txt'), 'ready')
"@ | Set-Content -LiteralPath (Join-Path $root 'Update-DevTools.ps1')
Invoke-HeadlessLaunch $bootstrapDirectory
Confirm-UpdateVersion $bootstrapDirectory
if (-not (Test-Path -LiteralPath (Join-Path $root 'bootstrapped.txt'))) { throw 'The installation fallback did not run.' }
Write-Output 'PASS: PowerShell fallback restored the installation pointer'
Write-Output ('Launch evidence: ' + $root)
