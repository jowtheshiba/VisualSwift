param([string]$ToolchainBin)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$ToolchainBin) { $ToolchainBin = Split-Path -Parent (Get-Command swift.exe -ErrorAction Stop).Source }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -property installationPath
$runner = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe'
if (!(Test-Path -LiteralPath $runner)) { throw 'The Visual Studio test platform is required.' }
$adapterProject = Join-Path $repository 'src\VisualSwift.TestAdapter\VisualSwift.TestAdapter.csproj'
dotnet build $adapterProject --nologo
if ($LASTEXITCODE -ne 0) { throw 'Test adapter build failed.' }
$adapter = Join-Path $repository 'src\VisualSwift.TestAdapter\bin\Debug\net472'
$results = Join-Path $repository 'artifacts\test-adapter-checks'
$workspace = Join-Path $results 'package with spaces'
New-Item -ItemType Directory -Force $workspace | Out-Null
Copy-Item -LiteralPath (Join-Path $repository 'samples\TestSmoke\Package.swift') -Destination $workspace -Force
foreach ($folder in @('Sources', 'Tests')) {
    Copy-Item -LiteralPath (Join-Path $repository "samples\TestSmoke\$folder") -Destination $workspace -Recurse -Force
}
$settingsDirectory = Join-Path $workspace '.vs\VisualSwift'
New-Item -ItemType Directory -Force $settingsDirectory | Out-Null
@{ toolchain = $ToolchainBin } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $settingsDirectory 'settings.json')
$source = Join-Path $workspace 'Package.swift'
$runId = [guid]::NewGuid().ToString('N')
$allLog = Join-Path $results "$runId-all.log"
& $runner $source "/TestAdapterPath:$adapter" "/Logger:trx;LogFileName=$runId-all.trx" "/ResultsDirectory:$results" *> $allLog
if ($LASTEXITCODE -ne 1) { throw "The intentional failures did not produce exit code 1. See $allLog" }
[xml]$report = Get-Content -LiteralPath (Join-Path $results "$runId-all.trx")
$cases = @($report.TestRun.Results.UnitTestResult)
$expected = @{
    'TestSmokeTests.CalculatorTests/testAddition' = 'Passed'
    'TestSmokeTests.CalculatorTests/testFailure' = 'Failed'
    'TestSmokeTests.CalculatorTests/testSkipped' = 'NotExecuted'
    'TestSmokeTests.addition()' = 'Passed'
    'TestSmokeTests.failure()' = 'Failed'
    'TestSmokeTests.skipped()' = 'NotExecuted'
    'TestSmokeTests.parameterized(value:)' = 'Passed'
}
if ($cases.Count -ne $expected.Count) { throw "Expected seven tests, got $($cases.Count). See $allLog" }
foreach ($name in $expected.Keys) {
    $match = @($cases | Where-Object testName -EQ $name)
    if ($match.Count -ne 1 -or $match[0].outcome -ne $expected[$name]) { throw "Unexpected result for $name. See $allLog" }
}
$failure = $cases | Where-Object testName -EQ 'TestSmokeTests.CalculatorTests/testFailure'
if ($failure.Output.ErrorInfo.Message -notmatch 'Intentional adapter smoke failure') { throw 'XCTest assertion details were lost.' }
$selectedLog = Join-Path $results "$runId-selected.log"
& $runner $source "/TestAdapterPath:$adapter" '/Tests:TestSmokeTests.parameterized(value:)' "/Logger:trx;LogFileName=$runId-selected.trx" "/ResultsDirectory:$results" *> $selectedLog
if ($LASTEXITCODE -ne 0) { throw "Selected test run failed. See $selectedLog" }
[xml]$selected = Get-Content -LiteralPath (Join-Path $results "$runId-selected.trx")
$selectedCases = @($selected.TestRun.Results.UnitTestResult)
if ($selectedCases.Count -ne 1 -or $selectedCases[0].testName -ne 'TestSmokeTests.parameterized(value:)' -or $selectedCases[0].outcome -ne 'Passed') { throw 'Run Selected executed unexpected tests.' }
$emptyLog = Join-Path $results "$runId-no-tests.log"
& $runner (Join-Path $repository 'samples\LspSmoke\Package.swift') "/TestAdapterPath:$adapter" /ListTests *> $emptyLog
if ($LASTEXITCODE -ne 0 -or (Get-Content -LiteralPath $emptyLog -Raw) -match 'Swift test discovery failed') { throw "A package without tests failed discovery. See $emptyLog" }
@{ passed = $true; discovered = $cases.Count; outcomes = $expected; selected = $selectedCases[0].testName; allResults = "$runId-all.trx"; selectedResults = "$runId-selected.trx" } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results 'report.json')
Write-Output 'Test adapter checks passed: seven discovered tests, Passed/Failed/Skipped for both frameworks, parameterized tests, Run Selected, assertion details, paths with spaces and packages without tests.'
