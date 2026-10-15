param([string]$ToolchainBin)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$ToolchainBin) { $ToolchainBin = Split-Path -Parent (Get-Command swift.exe -ErrorAction Stop).Source }
$swift = Join-Path $ToolchainBin 'swift.exe'
$env:PATH = $ToolchainBin + [IO.Path]::PathSeparator + $env:PATH
. (Join-Path $repository 'templates\Shared\WindowsSwiftEnvironment.ps1')
$cxxEnvironment = Get-WindowsSwiftEnvironment $ToolchainBin
foreach ($name in $cxxEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $cxxEnvironment[$name], 'Process') }
$package = Join-Path $repository 'samples\CxxInterop'
$scratch = Join-Path $repository 'artifacts\cxx-package'
& $swift build --package-path $package --scratch-path $scratch -c debug -Xswiftc -g -Xswiftc -use-ld=lld -Xcc -g -Xcc -gdwarf-4 -Xcxx -g -Xcxx -gdwarf-4 -Xlinker /debug:dwarf
if ($LASTEXITCODE -ne 0) { throw 'Swift + C++ debug build failed.' }
$bin = & $swift build --package-path $package --scratch-path $scratch -c debug --show-bin-path
if ($LASTEXITCODE -ne 0) { throw 'Swift + C++ executable path failed.' }
$result = & (Join-Path $bin 'CxxInterop.exe')
if ($LASTEXITCODE -ne 0 -or $result -ne 'Swift + C++: 42') { throw 'Swift + C++ returned an unexpected result.' }
dotnet run --project (Join-Path $repository 'tools\VisualSwift.DebugProbe') -- (Join-Path $ToolchainBin 'lldb-dap.exe') (Join-Path $bin 'CxxInterop.exe') $package (Join-Path $repository 'artifacts\cxx-smoke\debug.json') cxx
if ($LASTEXITCODE -ne 0) { throw 'Mixed-language debugging failed. See artifacts\cxx-smoke\debug.json.' }
& $swift build --package-path $package -c debug
if ($LASTEXITCODE -ne 0) { throw 'Swift + C++ LSP build failed.' }
dotnet run --project (Join-Path $repository 'tools\VisualSwift.LspProbe') -- (Join-Path $ToolchainBin 'sourcekit-lsp.exe') $package (Join-Path $repository 'artifacts\cxx-smoke\lsp.json') cxx
if ($LASTEXITCODE -ne 0) { throw 'Mixed-language completion or navigation failed. See artifacts\cxx-smoke\lsp.json.' }
