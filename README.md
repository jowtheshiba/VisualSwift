# VisualSwift

Swift language support for Microsoft Visual Studio on Windows, powered by SourceKit-LSP and Swift Package Manager.

The repository contains a working development VSIX. Completion, Quick Info, Go To Definition, and editor diagnostics have been verified in Visual Studio 2026 on Windows ARM64 with Swift 6.4. Swift Console Application and Swift Library templates now provide a SwiftPM package inside a Visual Studio Makefile project, with Build/Rebuild/Clean integration. A single Swift window provides toolchain selection, language-server management, package inspection, and output through a sidebar. A dedicated Swift project system remains subsequent work.

## Project templates

Install the VSIX and search for `Swift` in File > New > Project. Choose **Swift Console Application** or **Swift Library**. The library includes a public API and an XCTest example. Each template has a generated icon; the extension has its own VisualSwift icon.

The templates require Visual Studio C++ project support because they use its built-in Makefile project system. Configure the Windows Swift toolchain and SDK environment before launching the IDE. Standard Build/Rebuild/Clean commands delegate to SwiftPM, and Debug/Release controls its build configuration. Platform labels do not select a different Swift architecture. `Package.swift` remains authoritative.

Run console applications with `swift run`; run library tests with `swift test`. The generated `VisualSwift\Invoke-Swift.ps1` also supports Run and Test. For debugging, select an executable on **Extensions > Swift > Debug** and use **Start Debugging**, or enable F5 for that package. Native breakpoints, Call Stack, Locals, Watch, and stepping were verified in Visual Studio 2026 on Windows ARM64. See [debugging](docs/DEBUGGING.md). Test Explorer integration remains open.

See [template implementation and validation](docs/PROJECT_TEMPLATES.md) for requirements and limitations, and [icon assets](assets/README.md) for sources and generation prompts.

## Build and test

Prerequisites: Windows, Visual Studio, .NET SDK 10 for the probe, and an installed Windows Swift toolchain with its SDK environment configured. The extension targets .NET Framework 4.7.2; its SDK and build tools are pinned NuGet dependencies.

```powershell
.\scripts\build.ps1
.\scripts\test-lsp.ps1
.\scripts\test-debug.ps1
.\scripts\test-templates.ps1 -InstanceId YOUR_INSTANCE_ID -Platform ARM64
```

The VSIX is generated at `src\VisualSwift.VisualStudio\bin\Debug\net472\VisualSwift.VisualStudio.vsix`. The probe writes `artifacts\lsp-smoke.json`, including feature results and server exit status. An explicit toolchain can be tested with `-ToolchainBin`.

## Run the prototype

Deploy to a separate Visual Studio development profile using the instance ID returned by `vswhere.exe`:

```powershell
.\scripts\deploy-prototype.ps1 -InstanceId YOUR_INSTANCE_ID -RootSuffix VisualSwiftClean
$env:VISUALSWIFT_WORKSPACE = (Resolve-Path .\samples\LspSmoke).Path
& 'PATH_TO_VISUAL_STUDIO\Common7\IDE\devenv.exe' $env:VISUALSWIFT_WORKSPACE /RootSuffix VisualSwiftClean
```

Open `Sources\LspSmoke\main.swift` in Solution Explorer. Use Ctrl+Space for completion, hover for Quick Info, and F12 for Go To Definition. A temporary type error should produce an editor diagnostic.

Use **Edit > Go To > Go To All** (`Ctrl+,` with the default keyboard mapping) to search Swift symbols through the standard Code Search interface. Open a Swift file first to activate SourceKit-LSP. Search includes indexed files that are not open in the editor; select a result and press Enter to navigate. **Find All References** (`Shift+F12`) uses the same server index. Results can be incomplete while indexing is in progress. See [search validation](docs/SEARCH.md).

Use native **Rename...** (`Ctrl+R, Ctrl+R`) for Swift symbols. Method renames can include Swift argument labels. The extension watches package source-file changes and enables background indexing so newly added files enter cross-file Rename without a server restart. Allow indexing to finish; see [rename validation](docs/RENAME.md). The VSIX packages a modern Swift grammar with checks for Swift 6.4 constructs; see [syntax coverage and limitations](docs/SYNTAX_HIGHLIGHTING.md).

Open **Extensions > Swift** for the unified window with Toolchain, Language Server, Package, Debug, and Output pages. Save a global toolchain or a workspace override, validate it, and restart the server from the window. See [window behavior and settings](docs/PLUGIN_WINDOW.md). Workspace and global settings take priority over `VISUALSWIFT_TOOLCHAIN`; PATH is the final fallback and must contain both `swift.exe` and `sourcekit-lsp.exe` in one directory. `VISUALSWIFT_WORKSPACE` optionally pins one package root and must contain `Package.swift`; Open Folder supplies a root when the override is absent. Environment variables must be set before launching Visual Studio. SDK variables such as `SDKROOT` are inherited in this prototype.

Development logs are written to `%TEMP%\VisualSwift\client-<pid>.log`. They include protocol results and local paths. See [prototype validation](docs/PROTOTYPE_VALIDATION.md) for measured compatibility, technical decisions, and remaining checks.

## Intended features

- Swift editing with completion, navigation, diagnostics, and syntax highlighting.
- An English-language Swift window with a sidebar and selected toolchain summary.
- Toolchain discovery, selection, validation, and language server restart.
- Swift Package Manager build, run, and test commands integrated with Visual Studio.
- Swift console application and library project templates.
- Progressive integration with Solution Explorer, project properties, Test Explorer, and debugging.

Visual Studio 2026 is the primary target. Visual Studio 2022 and Windows x64 remain compatibility candidates awaiting IDE smoke tests.

See [the development plan](docs/DEVELOPMENT_PLAN.md) for architecture, delivery stages, and acceptance criteria.

## License

Copyright 2026 jowtheshiba.

Licensed under the [Apache License, Version 2.0](LICENSE). See [NOTICE](NOTICE).
