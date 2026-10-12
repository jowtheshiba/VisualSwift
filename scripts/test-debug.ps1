param([string]$ToolchainBin)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$ToolchainBin) { $ToolchainBin = Split-Path -Parent (Get-Command swift.exe -ErrorAction Stop).Source }
$swift = Join-Path $ToolchainBin 'swift.exe'
$package = Join-Path $repository 'samples\DebugSmoke'
$scratch = Join-Path $repository 'artifacts\debug-package'
& $swift build --package-path $package --scratch-path $scratch -c debug -Xswiftc -use-ld=lld -Xlinker /debug:dwarf
if ($LASTEXITCODE -ne 0) { throw 'Swift debug build failed.' }
$bin = & $swift build --package-path $package --scratch-path $scratch -c debug --show-bin-path
if ($LASTEXITCODE -ne 0) { throw 'Swift debug executable path failed.' }
dotnet run --project (Join-Path $repository 'tools\VisualSwift.DebugProbe') -- (Join-Path $ToolchainBin 'lldb-dap.exe') (Join-Path $bin 'DebugSmoke.exe') (Join-Path $package 'main.swift') (Join-Path $repository 'artifacts\debug-smoke\dap.json')
if ($LASTEXITCODE -ne 0) { throw 'Swift debug smoke failed. See artifacts\debug-smoke\dap.json.' }
