# VisualSwift

Swift language support for Microsoft Visual Studio on Windows, powered by SourceKit-LSP and Swift Package Manager.

The repository contains a working compatibility prototype. Completion, Quick Info, Go To Definition, and editor diagnostics have been verified in Visual Studio 2026 on Windows ARM64 with Swift 6.4. This is a development VSIX; toolchain selection UI, project templates, and IDE build commands are subsequent milestones.

## Build and test

Prerequisites: Windows, Visual Studio, .NET SDK 10 for the probe, and an installed Windows Swift toolchain with its SDK environment configured. The extension targets .NET Framework 4.7.2; its SDK and build tools are pinned NuGet dependencies.

```powershell
.\scripts\build.ps1
.\scripts\test-lsp.ps1
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

`VISUALSWIFT_TOOLCHAIN` optionally selects a toolchain root or its `usr\bin` directory. Otherwise the extension selects a PATH directory containing both `swift.exe` and `sourcekit-lsp.exe`. `VISUALSWIFT_WORKSPACE` optionally pins one package root and must contain `Package.swift`; Open Folder supplies a root when the override is absent. Environment variables must be set before launching Visual Studio. SDK variables such as `SDKROOT` are inherited in this prototype.

Development logs are written to `%TEMP%\VisualSwift\client-<pid>.log`. They include protocol results and local paths. See [prototype validation](docs/PROTOTYPE_VALIDATION.md) for measured compatibility, technical decisions, and remaining checks.

## Intended features

- Swift editing with completion, navigation, diagnostics, and syntax highlighting.
- An English-language Swift menu showing the selected toolchain.
- Toolchain discovery, selection, validation, and language server restart.
- Swift Package Manager build, run, and test commands integrated with Visual Studio.
- Swift console application and library project templates.
- Progressive integration with Solution Explorer, project properties, Test Explorer, and debugging.

Visual Studio 2026 is the primary target. Visual Studio 2022 and Windows x64 remain compatibility candidates awaiting IDE smoke tests.

See [the development plan](docs/DEVELOPMENT_PLAN.md) for architecture, delivery stages, and acceptance criteria.

## License

Copyright 2026 jowtheshiba.

Licensed under the [Apache License, Version 2.0](LICENSE). See [NOTICE](NOTICE).
