param([string]$ToolchainBin)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
Push-Location $repository
try {
    $probe = Join-Path $repository 'tools\VisualSwift.LspProbe'
    dotnet run --project $probe -- broker-recovery
    if ($LASTEXITCODE -ne 0) { throw 'Broker recovery checks failed.' }
    if (!$ToolchainBin) { $ToolchainBin = Split-Path -Parent (Get-Command swift.exe -ErrorAction Stop).Source }
    . (Join-Path $repository 'templates\Shared\WindowsSwiftEnvironment.ps1')
    $swiftEnvironment = Get-WindowsSwiftEnvironment $ToolchainBin
    foreach ($name in $swiftEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $swiftEnvironment[$name], 'Process') }
    dotnet run --project $probe -- (Join-Path $ToolchainBin 'sourcekit-lsp.exe') (Join-Path $repository 'artifacts\multi-project-recovery') (Join-Path $repository 'artifacts\multi-project-recovery.json') multi-project
    if ($LASTEXITCODE -ne 0) { throw 'SourceKit-LSP recovery checks failed. See artifacts\multi-project-recovery.json.' }
}
finally { Pop-Location }
