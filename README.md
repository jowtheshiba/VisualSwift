# VisualSwift

Version **1.2**.

Swift language support for Microsoft Visual Studio on Windows, powered by SourceKit-LSP and Swift Package Manager.

## Installation

Build Release installers with:

```powershell
.\scripts\build-installers.ps1
```

Packages are written to `artifacts/installers`:

- `VisualSwift-1.2.0-universal.vsix` for ARM64 and x86_64.
- `VisualSwift-1.2.0-arm64.vsix` for ARM64.
- `VisualSwift-1.2.0-x86_64.vsix` for x86_64.

Choose one package, close Visual Studio, double-click the VSIX, and select the target IDE instance. All variants share one extension identity and support upgrades from earlier versions. SHA-256 checksums and package validation results are included alongside the installers. Packages are unsigned; VSIX Installer may show an unverified publisher.

The manifest targets Visual Studio Community, Professional, and Enterprise 17.14 through 18.x. The measured runtime environment is Visual Studio 2026 on Windows ARM64 with Swift 6.4. Visual Studio 2022 and x86_64 runtime compatibility have not been verified.

Install the matching Windows Swift toolchain, SDK, and Visual Studio C++ build tools separately and configure their environment before launching Visual Studio. Swift executables are not bundled. Project templates additionally require Visual Studio C++ project support; existing packages can be edited using Open Folder.

To uninstall, open **Extensions > Manage Extensions > Installed**, select **VisualSwift**, and choose **Uninstall**. Close all Visual Studio windows so VSIX Installer can apply the removal, then restart the IDE. To update, close Visual Studio and install the newer VSIX; uninstalling the older version first is normally unnecessary because the extension identity is unchanged.

## Features

- Swift completion, syntax highlighting, Quick Info and diagnostics.
- Multiple Swift projects per solution, with independent language servers, toolchains, build profiles and Test Explorer containers. F5 follows the startup project.
- Code Search, Go To Definition, type navigation and Find All References.
- Call Hierarchy and Type Hierarchy in the Swift window, with source navigation and call sites.
- Rename across files.
- SourceKit-LSP Code Actions and compiler Fix-it through Quick Actions (`Ctrl+.`).
- Signature Help and configurable Inlay Hints.
- Manual Format Document and Format Selection.
- SwiftPM tests in Test Explorer (XCTest and Swift Testing).
- Console and library project templates with Build/Rebuild/Clean.
- **Add > New Item > Swift File** with SwiftPM target selection.
- **File > New > File > Swift File** for a standalone source file.
- SwiftPM dependencies in Solution Explorer: add packages and products, resolve, update and remove with manifest preview.
- **Swift Project Settings** in Solution Explorer: Debug/Release build flags, compilation conditions and launch profiles.
- LLDB debugging: F5, breakpoints, stepping, Call Stack, Locals and Watch.
- Automatic language server refresh when Swift files are added, removed or renamed.

Open **Extensions > Swift** for toolchain, language server, package, debug and output settings. Use the **Project** selector to inspect a specific Swift package; editor navigation also selects the corresponding project. Project folders inside solution folders are supported.

## Build and verification

Building requires Windows, Visual Studio, an installed Swift toolchain/SDK, and pinned NuGet dependencies. Probes require .NET SDK 10. The VSIX targets .NET Framework 4.7.2. The build script locates installed Visual Studio TestWindow interfaces with vswhere.

```powershell
.\scripts\build.ps1
.\scripts\build-installers.ps1
.\scripts\test-lsp.ps1
.\scripts\test-debug.ps1
.\scripts\test-tests.ps1
.\scripts\test-templates.ps1 -InstanceId YOUR_INSTANCE_ID -Platform ARM64
```

The development VSIX is in `src\VisualSwift.VisualStudio\bin\Debug\net472`. Probe reports are in `artifacts`. Test adapter checks expect intentional failures and skips in the smoke fixture. Installer checks validate contents, manifests, architecture targets, and AnyCPU assemblies; they do not establish runtime compatibility on untested machines.

The newest Signature Help, Inlay Hints, type navigation and Test Explorer additions passed LSP/core/VSTest checks. Their final IDE interaction checks remain pending because computer-use could not capture the IDE window.

To deploy to an isolated development profile:

```powershell
.\scripts\deploy-prototype.ps1 -InstanceId YOUR_INSTANCE_ID -RootSuffix VisualSwiftClean
```

Launch the corresponding Visual Studio instance with `/RootSuffix VisualSwiftClean` and open a SwiftPM package. Logs are written to `%TEMP%\VisualSwift\client-<pid>.log`; test XML attachments are retained under `<package>\.vs\VisualSwift\TestResults`.

## License

Copyright 2026 jowtheshiba. Original code is licensed under the [Apache License, Version 2.0](LICENSE). See [NOTICE](NOTICE).

The Swift TextMate grammar derives from jtbandes/swift-tmlanguage revision `88500a474365c5bda55588258cb585a1f275529e`, with adaptations for typed throws and module selectors. Its MIT license is preserved in `src/VisualSwift.VisualStudio/Grammars/LICENSE.swift-tmlanguage.md`. The test adapter bundles Newtonsoft.Json 13.0.3 with its MIT license preserved in `src/VisualSwift.TestAdapter/LICENSE.Newtonsoft.Json.md`.
