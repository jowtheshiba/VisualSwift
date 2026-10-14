param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repository 'artifacts\installers' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release
Add-Type -AssemblyName System.IO.Compression.FileSystem
$built = Join-Path $repository 'src\VisualSwift.VisualStudio\bin\Release\net472\VisualSwift.VisualStudio.vsix'
$manifestPath = Join-Path $repository 'src\VisualSwift.VisualStudio\source.extension.vsixmanifest'
[xml]$sourceManifest = Get-Content -LiteralPath $manifestPath
$version = $sourceManifest.PackageManifest.Metadata.Identity.Version
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null

function Set-PackageArchitecture([string]$Path, [string]$Architecture) {
    $archive = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($name in @('extension.vsixmanifest', 'source.extension.vsixmanifest')) {
            $entry = $archive.GetEntry($name)
            if (!$entry) { continue }
            $reader = [IO.StreamReader]::new($entry.Open())
            try { [xml]$document = $reader.ReadToEnd() } finally { $reader.Dispose() }
            foreach ($target in @($document.PackageManifest.Installation.InstallationTarget)) {
                if ($target.ProductArchitecture -ne $Architecture) { [void]$target.ParentNode.RemoveChild($target) }
            }
            $entry.Delete()
            $replacement = $archive.CreateEntry($name)
            $stream = $replacement.Open()
            try { $document.Save($stream) } finally { $stream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}

function Test-Installer([string]$Path, [string[]]$Architectures) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $required = @('extension.vsixmanifest', 'VisualSwift.VisualStudio.dll', 'TestAdapter/VisualSwift.TestAdapter.dll',
            'TestAdapter/Newtonsoft.Json.dll', 'TestAdapter/LICENSE.Newtonsoft.Json.md', 'LICENSE', 'NOTICE',
            'ProjectTemplates/SwiftConsole/SwiftConsole.vstemplate', 'ProjectTemplates/SwiftLibrary/SwiftLibrary.vstemplate',
            'Runtime/WindowsSwiftEnvironment.ps1', 'Runtime/Invoke-Swift.ps1', 'TestAdapter/Runtime/WindowsSwiftEnvironment.ps1',
            'ItemTemplates/SwiftFile/SwiftFile.vstemplate', 'ItemTemplates/SwiftFile/SwiftFile.swift',
            'NewFileTemplates/SwiftFile.swift', 'NewFileTemplates/SwiftFiles.vsdir',
            'ProjectTemplates/SwiftConsole/VisualSwift/WindowsSwiftEnvironment.ps1', 'ProjectTemplates/SwiftLibrary/VisualSwift/WindowsSwiftEnvironment.ps1',
            'ProjectTemplates/templateManifest0.noloc.vstman', 'Grammars/swift.tmLanguage.json')
        foreach ($name in $required) {
            if (!$archive.GetEntry($name)) { throw "Missing installer content: $name in $Path" }
        }
        $reader = [IO.StreamReader]::new($archive.GetEntry('extension.vsixmanifest').Open())
        try { [xml]$document = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $targets = @($document.PackageManifest.Installation.InstallationTarget)
        if ($targets.Count -ne 3 * $Architectures.Count) { throw "Unexpected installation targets in $Path" }
        foreach ($sku in @('Microsoft.VisualStudio.Community', 'Microsoft.VisualStudio.Pro', 'Microsoft.VisualStudio.Enterprise')) {
            foreach ($architecture in $Architectures) {
                if (@($targets | Where-Object { $_.Id -eq $sku -and $_.ProductArchitecture -eq $architecture }).Count -ne 1) { throw "Missing $sku/$architecture target in $Path" }
            }
        }
        foreach ($name in @('Icon', 'PreviewImage', 'License')) {
            $relative = $document.PackageManifest.Metadata.$name.Replace('\', '/')
            if (!$archive.GetEntry($relative)) { throw "Missing manifest $name asset: $relative" }
        }
        if ($document.PackageManifest.Metadata.Identity.Version -ne $version) { throw 'Installer version does not match the source manifest.' }
    }
    finally { $archive.Dispose() }
}

foreach ($assemblyName in @('VisualSwift.VisualStudio.dll', 'VisualSwift.TestAdapter.dll')) {
    $assemblyPath = if ($assemblyName -eq 'VisualSwift.VisualStudio.dll') { Join-Path $repository "src\VisualSwift.VisualStudio\bin\Release\net472\$assemblyName" } else { Join-Path $repository "src\VisualSwift.TestAdapter\bin\Release\net472\$assemblyName" }
    $assembly = [Reflection.Assembly]::LoadFile($assemblyPath)
    $kinds = [Reflection.PortableExecutableKinds]::NotAPortableExecutableImage
    $machine = [Reflection.ImageFileMachine]::I386
    $assembly.ManifestModule.GetPEKind([ref]$kinds, [ref]$machine)
    if (($kinds -band [Reflection.PortableExecutableKinds]::ILOnly) -eq 0 -or ($kinds -band [Reflection.PortableExecutableKinds]::Required32Bit) -ne 0 -or ($kinds -band [Reflection.PortableExecutableKinds]::PE32Plus) -ne 0) { throw "$assemblyName must be AnyCPU for ARM64 and x86_64." }
}

$packages = @()
foreach ($variant in @('universal', 'arm64', 'x86_64')) {
    $path = Join-Path $OutputDirectory "VisualSwift-$version-$variant.vsix"
    Copy-Item -LiteralPath $built -Destination $path -Force
    $architectures = @(if ($variant -eq 'universal') { 'amd64'; 'arm64' } elseif ($variant -eq 'x86_64') { 'amd64' } else { 'arm64' })
    if ($variant -ne 'universal') { Set-PackageArchitecture $path $architectures[0] }
    Test-Installer $path $architectures
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $packages += [pscustomobject]@{ file = [IO.Path]::GetFileName($path); architectures = $architectures; sha256 = $hash; bytes = (Get-Item -LiteralPath $path).Length }
}
$packages | ForEach-Object { "$($_.sha256)  $($_.file)" } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
@{ version = $version; assemblies = 'AnyCPU'; validationPassed = $true; packages = $packages } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'installers.json') -Encoding utf8
Write-Output "Validated installers: $OutputDirectory"
