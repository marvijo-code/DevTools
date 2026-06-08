param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root "DevTools.sln"
$outputExe = Join-Path $root "bin\DevTools.exe"

$runningApp = Get-Process DevTools -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $outputExe }

if ($runningApp) {
    Write-Host "Stopping running app..."
    $runningApp | Stop-Process -Force
    Start-Sleep -Milliseconds 750
}

Write-Host "Building DevTools ($Configuration)..."
dotnet build $solution -c $Configuration

if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed."
}

if (-not (Test-Path $outputExe)) {
    throw "Build completed, but the expected executable was not found at $outputExe"
}

Write-Host "Launching:"
Write-Host "  $outputExe"
Start-Process -FilePath $outputExe -WorkingDirectory (Split-Path $outputExe -Parent)
