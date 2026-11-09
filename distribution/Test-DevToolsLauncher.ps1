param([Parameter(Mandatory)][string]$LauncherPath)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('DevTools launch verification ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$launcher = Join-Path $root 'DevTools.Launcher.exe'
Copy-Item -LiteralPath $LauncherPath -Destination $launcher
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-DevTools.ps1') -Destination $root
(Get-Command pwsh -ErrorAction Stop).Source | Set-Content -LiteralPath (Join-Path $root 'powershell-path.txt') -Encoding utf8NoBOM
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
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$probe = Join-Path $root 'LaunchProbe.exe'
& $compiler /nologo /target:winexe ('/out:' + $probe) $source
if ($LASTEXITCODE -ne 0) { throw 'Launch probe compilation failed.' }
$initialHash = (Get-FileHash -LiteralPath $launcher).Hash
foreach ($version in @('v1.0.0', 'v1.0.1')) {
    $directory = Join-Path $root ('versions\' + $version)
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Copy-Item -LiteralPath $probe -Destination (Join-Path $directory 'DevTools.exe')
    'param([string]$InstallRoot)' | Set-Content -LiteralPath (Join-Path $directory 'Update-DevTools.ps1')
    @{version=$version; directory=$directory} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'current.json')
    # --no-admin keeps this test headless: the default is to elevate through a UAC prompt.
    $child = Start-Process -FilePath $launcher -ArgumentList '--no-admin' -WindowStyle Hidden -PassThru
    if (-not $child.WaitForExit(30000) -or $child.ExitCode -ne 0) { throw "Launcher failed for $version" }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath (Join-Path $directory 'launched.txt')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath (Join-Path $directory 'launched.txt'))) { throw "Wrong launch target for $version" }
    if ((Get-FileHash -LiteralPath $launcher).Hash -ne $initialHash) { throw 'Launcher changed during version transition.' }
    Write-Output "PASS: permanent launcher started $version"
}
Write-Output ('Launch evidence: ' + $root)
