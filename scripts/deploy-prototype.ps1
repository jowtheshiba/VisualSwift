param([Parameter(Mandatory = $true)][string]$InstanceId, [ValidatePattern('^VisualSwift[A-Za-z0-9]*$')][string]$RootSuffix = 'VisualSwift')

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$instances = & $vswhere -all -products '*' -format json | ConvertFrom-Json
$instance = @($instances | Where-Object instanceId -EQ $InstanceId)
if ($instance.Count -ne 1) { throw 'Select one installed Visual Studio instance using its vswhere instanceId.' }
$msbuild = Join-Path $instance[0].installationPath 'MSBuild\Current\Bin\MSBuild.exe'
$testWindow = Join-Path $instance[0].installationPath 'Common7\IDE\CommonExtensions\Microsoft\TestWindow'
& $msbuild (Join-Path $repository 'src\VisualSwift.VisualStudio\VisualSwift.VisualStudio.csproj') /restore /t:Build /p:Configuration=Debug /p:DeployExtension=true "/p:DeployTargetInstanceId=$InstanceId" "/p:VSSDKTargetPlatformRegRootSuffix=$RootSuffix" "/p:VisualStudioTestWindowPath=$testWindow" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Prototype deployment failed.' }
Write-Output "Deployed to the $RootSuffix test profile. Start devenv.exe with /RootSuffix $RootSuffix."
