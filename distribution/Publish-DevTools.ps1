param([Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Version)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repositoryRoot ('artifacts\releases\' + $Version)
$app = Join-Path $output 'app'
New-Item -ItemType Directory -Path $app -Force | Out-Null
dotnet publish (Join-Path $repositoryRoot 'DevTools\DevTools.csproj') -c Release -f net10.0-windows10.0.26100 -r win-x64 --self-contained true -p:PublishProfile=win-x64 -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:WindowsAppSDKSelfContained=true -p:WindowsPackageType=None -p:AppxPackageSigningEnabled=false -p:UseSharedCompilation=false -m:1 -o $app
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $app 'DevTools.exe'))) { throw 'Release executable is missing.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-DevTools.ps1'),(Join-Path $PSScriptRoot 'Update-DevTools.ps1'),(Join-Path $PSScriptRoot 'Install-DevTools.ps1'),(Join-Path $PSScriptRoot 'Register-DevTools.ps1') -Destination $app
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:x64 /reference:System.Windows.Forms.dll ('/win32icon:' + (Join-Path $app 'icon.ico')) ('/out:' + (Join-Path $app 'DevTools.Launcher.exe')) (Join-Path $PSScriptRoot 'DevTools.Launcher.cs') (Join-Path $repositoryRoot 'DevTools\Infrastructure\ShellIdentity.cs')
if ($LASTEXITCODE -ne 0) { throw 'Stable launcher build failed.' }
$commit = git -C $repositoryRoot rev-parse HEAD
@{ version=$Version; repository='marvijo-code/DevTools'; commit=$commit } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $app 'release.json') -Encoding utf8
$zip = Join-Path $output 'DevTools-win-x64.zip'
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
($hash + '  DevTools-win-x64.zip') | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Output $output
