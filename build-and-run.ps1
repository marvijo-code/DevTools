param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root "DevTools.sln"
$outputRoot = Join-Path $root "DevTools\bin\$Configuration"

# The root bin\ folder is a flat copy target that can hold stale, self-contained runtime files
# (coreclr.dll and friends). Launching that copy makes the apphost resolve from the app folder and
# fail. Always run the freshly built executable from the project's own bin folder instead.
$runningApp = Get-Process DevTools -ErrorAction SilentlyContinue |
    Where-Object { $_.StartTime -and $_.Path }

# Stop the running copy BEFORE the build: the apphost (DevTools.exe) is file-locked while it runs,
# so building first fails with MSB3027/MSB3021 on a rebuild.
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

$outputExe = Get-ChildItem -Path $outputRoot -Recurse -Filter "DevTools.exe" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\publish\\' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $outputExe) {
    throw "Build completed, but no DevTools.exe was found under $outputRoot"
}

Write-Host "Launching:"
Write-Host "  $outputExe"
Start-Process -FilePath $outputExe -WorkingDirectory (Split-Path $outputExe -Parent)