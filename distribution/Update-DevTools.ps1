param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'MarvijoDevTools'),
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$repository = 'marvijo-code/DevTools'
$assetName = 'DevTools-win-x64.zip'
New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
$statePath = Join-Path $InstallRoot 'update-state.json'
$currentPath = Join-Path $InstallRoot 'current.json'
$updateLock = $null
$downloadFolder = $null

function Invoke-GitHub([string[]]$Arguments, [int]$TimeoutSeconds = 30) {
    $ghPath = (Get-Command gh -ErrorAction Stop).Source
    $start = [Diagnostics.ProcessStartInfo]::new($ghPath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    try {
        $output = $child.StandardOutput.ReadToEndAsync()
        $errors = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit($TimeoutSeconds * 1000)) {
            $child.Kill($true)
            throw 'GitHub update request timed out. The installed version is unchanged.'
        }
        if ($child.ExitCode -ne 0) {
            throw 'GitHub update request failed. Check gh authentication and network access.'
        }
        return $output.GetAwaiter().GetResult()
    }
    finally { $child.Dispose() }
}

function Save-JsonAtomic([string]$Path, $Value) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $Value | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

try {
    try {
        $updateLock = [IO.File]::Open((Join-Path $InstallRoot 'update.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    }
    catch [IO.IOException] { exit 0 }

    if (-not $Force -and (Test-Path -LiteralPath $statePath)) {
        $previousCheck = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if ($previousCheck.checkedAt -and ([DateTimeOffset]::UtcNow - [DateTimeOffset]::Parse($previousCheck.checkedAt)).TotalHours -lt 6) {
            exit 0
        }
    }
    $release = Invoke-GitHub @('release', 'view', '--repo', $repository, '--json', 'tagName,assets,isDraft,isPrerelease') | ConvertFrom-Json
    if ($release.isDraft -or $release.isPrerelease -or $release.tagName -notmatch '^v\d+\.\d+\.\d+$') {
        throw 'No supported stable release is available.'
    }
    $installed = if (Test-Path -LiteralPath $currentPath) { Get-Content -LiteralPath $currentPath -Raw | ConvertFrom-Json } else { $null }
    if ($installed -and ([version]$installed.version.TrimStart('v')) -ge ([version]$release.tagName.TrimStart('v'))) {
        Save-JsonAtomic $statePath @{ checkedAt = [DateTimeOffset]::UtcNow.ToString('o'); status = 'current'; version = $installed.version }
        exit 0
    }
    if ($assetName -notin $release.assets.name -or 'SHA256SUMS.txt' -notin $release.assets.name) {
        throw 'The release is missing its Windows package or checksum.'
    }
    $downloadFolder = Join-Path $InstallRoot ('downloads\' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $downloadFolder -Force | Out-Null
    Invoke-GitHub @('release', 'download', $release.tagName, '--repo', $repository, '--pattern', $assetName, '--pattern', 'SHA256SUMS.txt', '--dir', $downloadFolder) 180 | Out-Null
    $checksumLines = Get-Content -LiteralPath (Join-Path $downloadFolder 'SHA256SUMS.txt')
    $checksumLine = @($checksumLines | Where-Object { $_ -match ('^[0-9a-fA-F]{64}\s+\*?' + [regex]::Escape($assetName) + '$') })
    if ($checksumLine.Count -ne 1) { throw 'The release checksum is missing or ambiguous.' }
    $expectedHash = ($checksumLine[0] -split '\s+')[0]
    $zipPath = Join-Path $downloadFolder $assetName
    if ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash -ne $expectedHash) {
        throw 'The downloaded package failed checksum verification.'
    }
    $staging = Join-Path $downloadFolder 'extracted'
    Expand-Archive -LiteralPath $zipPath -DestinationPath $staging
    if (-not (Test-Path -LiteralPath (Join-Path $staging 'DevTools.exe'))) {
        throw 'The package does not contain DevTools.exe.'
    }
    $manifest = Get-Content -LiteralPath (Join-Path $staging 'release.json') -Raw | ConvertFrom-Json
    if ($manifest.version -ne $release.tagName -or $manifest.repository -ne $repository) {
        throw 'The package identity does not match the selected release.'
    }
    $versionsPath = Join-Path $InstallRoot 'versions'
    New-Item -ItemType Directory -Path $versionsPath -Force | Out-Null
    $destination = Join-Path $versionsPath ($release.tagName + '-' + $expectedHash.Substring(0,12).ToLowerInvariant())
    if (-not (Test-Path -LiteralPath $destination)) {
        Move-Item -LiteralPath $staging -Destination $destination
    }
    Save-JsonAtomic $currentPath @{ version = $release.tagName; directory = $destination; sha256 = $expectedHash }
    Save-JsonAtomic $statePath @{ checkedAt = [DateTimeOffset]::UtcNow.ToString('o'); status = 'ready'; version = $release.tagName }
    Write-Output ('Ready for the next launch: ' + $release.tagName)
}
catch {
    Save-JsonAtomic $statePath @{ checkedAt = [DateTimeOffset]::UtcNow.ToString('o'); status = 'error'; message = $_.Exception.Message }
    Write-Error $_.Exception.Message
    exit 1
}
finally {
    if ($updateLock) { $updateLock.Dispose() }
    if ($downloadFolder -and (Test-Path -LiteralPath $downloadFolder)) {
        $resolvedDownload = [IO.Path]::GetFullPath($downloadFolder)
        $downloadBoundary = [IO.Path]::GetFullPath((Join-Path $InstallRoot 'downloads')) + [IO.Path]::DirectorySeparatorChar
        if ($resolvedDownload.StartsWith($downloadBoundary, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedDownload -Recurse -Force
        }
    }
}
