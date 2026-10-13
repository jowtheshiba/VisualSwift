param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -property installationPath
if (!$installation) { throw 'Visual Studio is required to build the Test Explorer integration.' }
$testWindow = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\TestWindow'
dotnet build (Join-Path $repository 'src\VisualSwift.VisualStudio\VisualSwift.VisualStudio.csproj') -c $Configuration --nologo "-p:VisualStudioTestWindowPath=$testWindow"
if ($LASTEXITCODE -ne 0) { throw 'VSIX build failed.' }
