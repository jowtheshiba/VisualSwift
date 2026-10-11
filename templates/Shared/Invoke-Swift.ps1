param([ValidateSet('Build', 'Rebuild', 'Clean', 'Run', 'Test')][string]$Command = 'Build', [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
try {
    $package = Split-Path -Parent $PSScriptRoot
    $configured = $env:VISUALSWIFT_TOOLCHAIN
    $globalSettings = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'VisualSwift\settings.json'
    $workspaceSettings = Join-Path $package '.vs\VisualSwift\settings.json'
    foreach ($settingsPath in @($globalSettings, $workspaceSettings)) {
        if (Test-Path -LiteralPath $settingsPath) {
            $savedToolchain = (Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json).toolchain
            if (![string]::IsNullOrWhiteSpace($savedToolchain)) { $configured = $savedToolchain }
        }
    }
    $swiftBin = $null
    if ($configured) {
        foreach ($candidate in @($configured, (Join-Path $configured 'usr\bin'))) {
            if ((Test-Path -LiteralPath (Join-Path $candidate 'swift.exe')) -and (Test-Path -LiteralPath (Join-Path $candidate 'sourcekit-lsp.exe'))) {
                $swiftBin = [System.IO.Path]::GetFullPath($candidate)
                break
            }
        }
        if (!$swiftBin) { throw 'The selected Swift toolchain must contain swift.exe and sourcekit-lsp.exe in its bin directory.' }
    }
    else {
        foreach ($entry in $env:PATH.Split([System.IO.Path]::PathSeparator)) {
            $candidate = $entry.Trim().Trim('"')
            if ($candidate -and (Test-Path -LiteralPath (Join-Path $candidate 'swift.exe')) -and (Test-Path -LiteralPath (Join-Path $candidate 'sourcekit-lsp.exe'))) {
                $swiftBin = $candidate
                break
            }
        }
        if (!$swiftBin) { throw 'Swift was not found. Configure PATH or VISUALSWIFT_TOOLCHAIN and restart Visual Studio.' }
    }
    $env:PATH = $swiftBin + [System.IO.Path]::PathSeparator + $env:PATH
    $swift = Join-Path $swiftBin 'swift.exe'
    $buildConfiguration = $Configuration.ToLowerInvariant()
    if ($Command -in @('Clean', 'Rebuild')) {
        & $swift package --package-path $package clean
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        if ($Command -eq 'Clean') { exit 0 }
    }
    $operation = switch ($Command) { 'Run' { 'run' } 'Test' { 'test' } default { 'build' } }
    & $swift $operation --package-path $package --configuration $buildConfiguration
    exit $LASTEXITCODE
}
catch {
    [Console]::Error.WriteLine('VisualSwift: ' + $_.Exception.Message)
    exit 1
}
