# VisualSwift

Version **1.0**.

Swift language support for Microsoft Visual Studio on Windows, powered by SourceKit-LSP and Swift Package Manager.

## Installation

Build Release installers with:

```powershell
.\scripts\build-installers.ps1
```

Packages are written to `artifacts/installers`:

- `VisualSwift-1.0.0-universal.vsix` for ARM64 and x86_64.
- `VisualSwift-1.0.0-arm64.vsix` for ARM64.
- `VisualSwift-1.0.0-x86_64.vsix` for x86_64.

Choose one package, close Visual Studio, double-click the VSIX, and select the target IDE instance. All variants share one extension identity and support upgrades from earlier versions. SHA-256 checksums and package validation results are included alongside the installers. Packages are unsigned; VSIX Installer may show an unverified publisher.

The manifest targets Visual Studio Community, Professional, and Enterprise 17.14 through 18.x. The measured runtime environment is Visual Studio 2026 on Windows ARM64 with Swift 6.4. Visual Studio 2022 and x86_64 runtime compatibility have not been verified.

Install the matching Windows Swift toolchain and SDK separately and configure their environment before launching Visual Studio. Swift executables are not bundled. Project templates additionally require Visual Studio C++ project support; existing packages can be edited using Open Folder.

Uninstall through Visual Studio's extension management window and restart the IDE.

## Features

- Completion, Quick Info, diagnostics, Swift syntax highlighting, Go To Definition, and Find All References.
- Native Code Search for indexed Swift symbols, including unopened files.
- Native Rename with cross-file edits and background indexing.
- Signature Help with `Ctrl+Shift+Space`, Go To Implementation, and Go To Type Definition.
- Inlay Hints settings on the Language Server page. Swift 6.4 SourceKit-LSP provides inferred types; parameter hints depend on toolchain support.
- Manual Format Document (`Ctrl+K, Ctrl+D`) and Format Selection (`Ctrl+K, Ctrl+F`), respecting `.swift-format` and editor Undo. Format on Save is not implemented.
- SwiftPM tests through native Test Explorer: XCTest and Swift Testing, Run All/Selected, outcomes, assertion details and output. Parameterized Swift Testing cases appear as one test item. Test debugging and coverage are not implemented.
- Swift Console Application and Swift Library project templates with Build/Rebuild/Clean integration.
- LLDB debugging with F5, breakpoints, stepping, Call Stack, Locals and Watch.

Open **Extensions > Swift** for the unified window with Toolchain, Language Server, Package, Debug, and Output pages. Select and validate a toolchain, manage the server, configure hints, inspect the package, and select a debugging product.

Toolchain selection uses workspace settings, then global settings, then `VISUALSWIFT_TOOLCHAIN`, then PATH. Settings are stored in `<package>\.vs\VisualSwift\settings.json` and `%LOCALAPPDATA%\VisualSwift\settings.json`. Restart the server after changing its toolchain. SDK variables such as `SDKROOT` are inherited from the IDE process.

`VISUALSWIFT_WORKSPACE` optionally pins one package root containing `Package.swift`. Otherwise the active document or solution supplies the package context. The extension currently operates on one active package. Allow indexing to finish before cross-file navigation or refactoring.

Search for **Swift** in **File > New > Project** to create a console application or library. Templates use the Visual Studio Makefile project system; `Package.swift` remains authoritative. Debug/Release selects the SwiftPM build configuration. The platform label does not switch the Swift compiler architecture.

For debugging, select an executable on the Debug page, save its options, and enable F5 for the package or use Start Debugging. Debug builds use LLVM/DWARF for LLDB on Windows. Tests use the package's selected toolchain and inherited SDK environment.

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
