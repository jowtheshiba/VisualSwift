param([string]$EnvironmentToolchainBin)

function Get-WindowsSwiftEnvironment([string]$BinDirectory) {
    $compiler = Join-Path $BinDirectory 'swiftc.exe'
    $targetOutput = & $compiler -print-target-info
    if ($LASTEXITCODE -ne 0) { throw 'The selected Swift compiler could not report its target architecture.' }
    $triple = ($targetOutput | Out-String | ConvertFrom-Json).target.triple
    $targetArchitecture = if ($triple -match '^aarch64-.*windows') { 'arm64' } elseif ($triple -match '^x86_64-.*windows') { 'x64' } else { throw "Unsupported Windows Swift target: $triple" }
    $developerCommand = if ($env:VSINSTALLDIR) { Join-Path $env:VSINSTALLDIR 'Common7\Tools\VsDevCmd.bat' } else { $null }
    if (!$developerCommand -or !(Test-Path -LiteralPath $developerCommand)) {
        $locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (!(Test-Path -LiteralPath $locator)) { throw 'Visual Studio C++ build tools are required to build Windows Swift packages.' }
        $installation = & $locator -latest -products '*' -property installationPath
        if (!$installation) { throw 'Visual Studio C++ build tools were not found.' }
        $developerCommand = Join-Path $installation 'Common7\Tools\VsDevCmd.bat'
    }
    $hostArchitecture = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -eq 'Arm64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64' -or $env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = Join-Path ([Environment]::GetFolderPath('System')) 'cmd.exe'
    $info.Arguments = '/d /s /c ""' + $developerCommand + '" -no_logo -arch=' + $targetArchitecture + ' -host_arch=' + $hostArchitecture + ' >nul && set"'
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($name in @($info.EnvironmentVariables.Keys)) {
        if ($name -like 'VSCMD_*' -or $name -eq '__VSCMD_PREINIT_PATH' -or $name -in @('LIB', 'LIBPATH')) { $info.EnvironmentVariables.Remove($name) }
    }
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $info
    try {
        [void]$process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Visual Studio build environment initialization timed out.' }
        $text = $output.GetAwaiter().GetResult()
        $errorText = $errors.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Cannot initialize MSVC for $targetArchitecture. Install the matching C++ build tools. $errorText" }
        $environment = @{}
        foreach ($line in $text -split '\r?\n') {
            $separator = $line.IndexOf('=')
            if ($separator -gt 0) { $environment[$line.Substring(0, $separator)] = $line.Substring($separator + 1) }
        }
        if ($environment['VSCMD_ARG_TGT_ARCH'] -ne $targetArchitecture) { throw "MSVC did not initialize the requested $targetArchitecture target." }
        $libraries = @($environment['LIB'] -split ';' | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ 'msvcrt.lib')) })
        if (!$libraries -or $libraries[0] -notmatch ('[\\/]' + $targetArchitecture + '[\\/]?$')) { throw "The $targetArchitecture MSVC runtime libraries are missing. Install the matching Visual Studio C++ tools." }
        $environment['PATH'] = $BinDirectory + [IO.Path]::PathSeparator + $environment['PATH']
        $result = @{}
        foreach ($name in $environment.Keys) {
            if ($name -in @('PATH', 'LIB', 'LIBPATH', 'INCLUDE', 'UniversalCRTSdkDir', 'ExtensionSdkDir', '__VSCMD_PREINIT_PATH') -or $name -like 'VS*' -or $name -like 'VC*' -or $name -like 'WindowsSDK*' -or $name -like 'UCRT*') { $result[$name] = $environment[$name] }
        }
        return $result
    }
    finally { $process.Dispose() }
}

if ($EnvironmentToolchainBin) {
    try { Get-WindowsSwiftEnvironment $EnvironmentToolchainBin | ConvertTo-Json -Compress }
    catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
}
