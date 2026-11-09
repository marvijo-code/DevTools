<#
.SYNOPSIS
    Headless test for the "start as administrator" decision in Start-DevTools.ps1.

.DESCRIPTION
    Dot-sources Start-DevTools.ps1 to load Get-DevToolsRunAsAdministrator, then
    asserts the decision against a throwaway settings directory in %TEMP%. The real
    %LOCALAPPDATA%\DevTools\settings.json is never read or written - it holds the
    encrypted GitHub token.

.EXAMPLE
    pwsh -NoProfile -File C:\dev\DevTools\distribution\Test-DevToolsLaunchPreference.ps1
#>
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'Start-DevTools.ps1'
. $script
if (-not (Get-Command Get-DevToolsRunAsAdministrator -ErrorAction SilentlyContinue)) {
    throw 'Start-DevTools.ps1 did not expose Get-DevToolsRunAsAdministrator when dot-sourced.'
}

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('devtools-launch-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
$settingsPath = Join-Path $sandbox 'settings.json'
$failures = New-Object System.Collections.Generic.List[string]
function Assert([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Output "PASS: $Message" }
    else { Write-Output "FAIL: $Message"; $script:failures.Add($Message) }
}
function Test-Preference([switch]$NoElevate, [switch]$Elevate) {
    return Get-DevToolsRunAsAdministrator -NoElevate:$NoElevate -Elevate:$Elevate -SettingsDirectory $sandbox
}

try {
    # Missing settings.json keeps the administrator default.
    Assert (Test-Preference) 'missing settings.json starts as administrator'

    # A missing property also keeps the default: releases before this feature never wrote it.
    '{"ActiveTab":"DiskUsage"}' | Set-Content -LiteralPath $settingsPath -Encoding utf8
    Assert (Test-Preference) 'settings.json without RunAsAdministrator starts as administrator'

    '{"RunAsAdministrator":false}' | Set-Content -LiteralPath $settingsPath -Encoding utf8
    Assert (-not (Test-Preference)) 'RunAsAdministrator=false starts without administrator rights'

    '{"RunAsAdministrator":true}' | Set-Content -LiteralPath $settingsPath -Encoding utf8
    Assert (Test-Preference) 'RunAsAdministrator=true starts as administrator'

    # Unreadable JSON fails closed to the administrator default.
    '{ not json' | Set-Content -LiteralPath $settingsPath -Encoding utf8
    Assert (Test-Preference) 'unreadable settings.json starts as administrator'

    # Per-launch overrides win over the stored preference.
    '{"RunAsAdministrator":false}' | Set-Content -LiteralPath $settingsPath -Encoding utf8
    Assert (Test-Preference -Elevate) '-Elevate overrides a stored opt-out'
    '{"RunAsAdministrator":true}' | Set-Content -LiteralPath $settingsPath -Encoding utf8
    Assert (-not (Test-Preference -NoElevate)) '-NoElevate overrides a stored opt-in'
}
finally {
    if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Output ''
if ($failures.Count) {
    Write-Output ('' + $failures.Count + ' FAILED')
    exit 1
}
Write-Output 'All launch-preference assertions passed.'
