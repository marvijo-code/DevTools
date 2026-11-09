# Install the repository git hooks (secret scan before every commit).
#
#   pwsh -NoProfile -File .\scripts\Install-GitHooks.ps1
#
# On Linux or macOS the same effect is:
#
#   git config core.hooksPath .githooks
param()

$ErrorActionPreference = "Stop"

$root = git rev-parse --show-toplevel 2>$null
if (-not $root) {
    throw "Run this script inside the DevTools repository."
}

git -C $root config core.hooksPath .githooks

$hook = Join-Path $root ".githooks/pre-commit"
if (-not (Test-Path $hook)) {
    throw "Expected hook not found at $hook"
}

Write-Host "Installed: core.hooksPath = .githooks"
Write-Host "Hook: $hook"
Write-Host "Every commit now runs a secret scan on the staged changes."
