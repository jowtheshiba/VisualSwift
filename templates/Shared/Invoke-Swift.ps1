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
    . (Join-Path $PSScriptRoot 'WindowsSwiftEnvironment.ps1')
    $environment = Get-WindowsSwiftEnvironment $swiftBin
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process') }
    Write-Output ('VisualSwift: Swift/MSVC target ' + $environment['VSCMD_ARG_TGT_ARCH'])
    $buildConfiguration = $Configuration.ToLowerInvariant()
    $profile = $null
    if (Test-Path -LiteralPath $workspaceSettings) { $profile = (Get-Content -Raw -LiteralPath $workspaceSettings | ConvertFrom-Json).projectProfiles.$Configuration }
    $buildFlags = [Collections.Generic.List[string]]::new()
    if ($profile) {
        if ($profile.Optimization -and $profile.Optimization -ne 'Default') { $buildFlags.Add('-Xswiftc'); $buildFlags.Add($profile.Optimization) }
        foreach ($condition in $profile.Conditions) { $buildFlags.Add('-Xswiftc'); $buildFlags.Add('-D' + $condition) }
        foreach ($mapping in @(@('SwiftFlags', '-Xswiftc'), @('CFlags', '-Xcc'), @('CxxFlags', '-Xcxx'), @('LinkerFlags', '-Xlinker'))) {
            foreach ($flag in $profile.($mapping[0])) { $buildFlags.Add($mapping[1]); $buildFlags.Add($flag) }
        }
    }
    if ($Command -in @('Clean', 'Rebuild')) {
        & $swift package --package-path $package clean
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        if ($Command -eq 'Clean') { exit 0 }
    }
    $operation = switch ($Command) { 'Run' { 'run' } 'Test' { 'test' } default { 'build' } }
    $swiftArguments = @($operation, '--package-path', $package, '--configuration', $buildConfiguration) + $buildFlags.ToArray()
    if ($Command -eq 'Run' -and $profile) {
        foreach ($property in $profile.Environment.PSObject.Properties) { [Environment]::SetEnvironmentVariable($property.Name, [string]$property.Value, 'Process') }
        if ($profile.WorkingDirectory) {
            $workingDirectory = if ([IO.Path]::IsPathRooted($profile.WorkingDirectory)) { $profile.WorkingDirectory } else { Join-Path $package $profile.WorkingDirectory }
            Set-Location -LiteralPath ([IO.Path]::GetFullPath($workingDirectory))
        }
        if ($profile.BuildBeforeLaunch -eq $false) { $swiftArguments += '--skip-build' }
        if ($profile.Product) { $swiftArguments += $profile.Product }
        $swiftArguments += @($profile.Arguments)
    }
    & $swift @swiftArguments
    exit $LASTEXITCODE
}
catch {
    [Console]::Error.WriteLine('VisualSwift: ' + $_.Exception.Message)
    exit 1
}
