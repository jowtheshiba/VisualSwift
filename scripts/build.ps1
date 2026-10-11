param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $repository 'src\VisualSwift.VisualStudio\VisualSwift.VisualStudio.csproj') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'VSIX build failed.' }
