param([string]$ToolchainBin)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$ToolchainBin) { $ToolchainBin = Split-Path -Parent (Get-Command swift.exe -ErrorAction Stop).Source }
$swiftExecutable = Join-Path $ToolchainBin 'swift.exe'
$serverExecutable = Join-Path $ToolchainBin 'sourcekit-lsp.exe'
if (!(Test-Path -LiteralPath $serverExecutable)) { throw 'sourcekit-lsp.exe was not found in the selected toolchain.' }
& $swiftExecutable --version
if ($LASTEXITCODE -ne 0) { throw 'Swift could not be started.' }
$packageDirectory = Join-Path $repository 'samples\LspSmoke'
& $swiftExecutable build --package-path $packageDirectory
if ($LASTEXITCODE -ne 0) { throw 'Swift smoke package build failed.' }
dotnet run --project (Join-Path $repository 'tools\VisualSwift.LspProbe') -- $serverExecutable $packageDirectory (Join-Path $repository 'artifacts\lsp-smoke.json')
if ($LASTEXITCODE -ne 0) { throw 'LSP smoke checks failed. See artifacts\lsp-smoke.json.' }
