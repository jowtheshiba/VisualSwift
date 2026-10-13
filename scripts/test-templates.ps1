param([Parameter(Mandatory = $true)][string]$InstanceId, [ValidateSet('ARM64', 'x64')][string]$Platform = 'ARM64')

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$instance = @((& $vswhere -all -products '*' -format json | ConvertFrom-Json) | Where-Object instanceId -EQ $InstanceId)
if ($instance.Count -ne 1) { throw 'Select an installed Visual Studio instance using its vswhere instanceId.' }
$msbuild = Join-Path $instance[0].installationPath 'MSBuild\Current\Bin\MSBuild.exe'
$runDirectory = Join-Path $repository ('artifacts\template-smoke\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$results = @()
foreach ($template in @('SwiftConsole', 'SwiftLibrary')) {
    $name = "${template}Smoke"
    $destination = Join-Path $runDirectory "$template with spaces"
    $archive = Join-Path $repository "src\VisualSwift.VisualStudio\obj\Debug\net472\ProjectTemplates\$template.zip"
    $staging = Join-Path $runDirectory "$template archive"
    [System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $staging)
    [xml]$definition = Get-Content (Join-Path $staging "$template.vstemplate") -Raw
    if ($definition.VSTemplate.TemplateData.LanguageTag -ne 'Swift') { throw 'Swift template language tag is missing.' }
    $templateProject = $definition.VSTemplate.TemplateContent.Project
    if ($templateProject.Folder) { throw 'VC templates must declare explicit relative ProjectItem paths.' }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $staging $templateProject.File) -Destination (Join-Path $destination "$name.vcxproj")
    foreach ($item in $templateProject.ProjectItem) {
        $source = Join-Path $staging $item.InnerText
        if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Template source missing: $source" }
        $target = Join-Path $destination $item.TargetFileName
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
    }
    foreach ($relative in @('Package.swift', "$name.vcxproj")) {
        $file = Join-Path $destination $relative
        $text = [System.IO.File]::ReadAllText($file).Replace('$safeprojectname$', $name).Replace('$projectname$', $name).Replace('$guid1$', [Guid]::NewGuid().ToString())
        [System.IO.File]::WriteAllText($file, $text, (New-Object System.Text.UTF8Encoding($false)))
    }
    $project = Join-Path $destination "$name.vcxproj"
    if ($template -eq 'SwiftConsole') {
        $projectText = [IO.File]::ReadAllText($project)
        if (!$projectText.Contains("$name.exe</NMakeOutput>") -or !$projectText.Contains('<LocalDebuggerCommand>$(NMakeOutput)</LocalDebuggerCommand>')) { throw 'The console launch target must be the Swift executable.' }
    }
    & $msbuild $project /t:Build /p:Configuration=Debug "/p:Platform=$Platform" /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "$template Debug build failed." }
    if ($template -eq 'SwiftConsole' -and !(Test-Path -LiteralPath (Join-Path $destination ".build\debug\$name.exe") -PathType Leaf)) { throw 'The declared console launch executable does not exist.' }
    & $msbuild $project /t:Rebuild /p:Configuration=Release "/p:Platform=$Platform" /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "$template Release rebuild failed." }
    $action = if ($template -eq 'SwiftConsole') { 'Run' } else { 'Test' }
    & (Join-Path $destination 'VisualSwift\Invoke-Swift.ps1') -Command $action -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "$template $action failed." }
    & $msbuild $project /t:Clean /p:Configuration=Release "/p:Platform=$Platform" /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "$template Clean failed." }
    $results += [pscustomobject]@{ template = $template; directory = $destination; debugBuild = $true; releaseRebuild = $true; action = $action; actionPassed = $true; clean = $true }
}
$report = [pscustomobject]@{ passed = $true; platform = $Platform; instanceId = $InstanceId; results = $results }
$report | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $repository 'artifacts\template-smoke.json') -Encoding UTF8
Write-Output 'Template smoke checks passed. See artifacts\template-smoke.json.'
