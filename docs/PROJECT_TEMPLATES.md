# Swift project templates

The 0.2.0 development VSIX includes Swift Console Application and Swift Library project templates, registered as Microsoft.VisualStudio.ProjectTemplate assets. They have English names, descriptions, and generated icons. Search for Swift in New Project.

## Project model

Both templates create a Windows SwiftPM package and a vcxproj Makefile wrapper that can participate in a Visual Studio solution. This uses the installed C++ project system; it does not compile Swift as C++ or generate a second dependency model. Visual Studio C++ project support and Windows Swift SDK prerequisites must be installed separately.

Build, Rebuild, and Clean call a package-local VisualSwift/Invoke-Swift.ps1 script. It uses VISUALSWIFT_TOOLCHAIN when configured, otherwise selects a PATH directory containing both swift.exe and sourcekit-lsp.exe. It prepends that directory to PATH and inherits the SDK environment. Failed Swift commands return their exit status to MSBuild.

Debug and Release map to SwiftPM debug and release. ARM64 and x64 project configurations are provided to fit solution configurations; the selected Swift toolchain determines the actual compilation architecture. Switching the platform label does not cross-compile.

The application has a product named after the safe project name and an App target with main.swift. The library has a product named after the safe project name, a Library target with a public Greeter, and a LibraryTests XCTest target. Fixed valid module names avoid template substitutions producing invalid Swift import identifiers. Rename target names, directories, and test imports together when choosing your own module names.

Source files and Package.swift appear in Solution Explorer through explicit project items. For new files, use Show All Files / Include In Project; SwiftPM discovers the package source folders independently. Explicit file paths also ensure the VC template engine copies sources when creating a project.

## Validation

The automated smoke script consumes the built template ZIP files, substitutes project names and GUIDs, and materializes each package in a path containing spaces. On Visual Studio 2026 18.10.3 / Windows ARM64 / Swift 6.4 it verified:

- MSBuild Debug Build and Release Rebuild for both projects.
- Application execution, producing Hello, Swift!.
- Library XCTest execution: one test passed with zero failures.
- MSBuild Clean for both projects.
- VSIX packaging of both template definitions, sources, scripts, icons, and template catalog.

The Swift SDK emitted a wchar_t modularization warning while compiling XCTest with the installed assertions toolchain; the test build and execution succeeded.

Run scripts/test-templates.ps1 with an installed vswhere instance ID and the desired platform label after scripts/build.ps1. Generated fixtures and the report at artifacts/template-smoke.json are excluded from Git.

## Scope limits

These templates provide an initial solution-compatible wrapper. A dedicated Swift project factory, Swift project properties, dependency hierarchy, automatic synchronization, standard run commands, debugging, and Test Explorer remain future work.

The current LSP prototype supports one package root. Open the package as a folder or set VISUALSWIFT_WORKSPACE before launching the IDE when the solution root differs from the package root. Multi-package language-server sessions are not yet implemented.

VS 2022, x64 execution, normal-profile VSIX upgrade/uninstall, and template creation on a clean external machine remain unverified. During local development, updating a previously used experimental profile produced VSSDK1031; deployment to a fresh VisualSwiftTemplates profile succeeded. Use a fresh development RootSuffix when the SDK reports stale extension registration.

## Build and asset sources

Template definitions and project wrappers live in templates/SwiftConsole and templates/SwiftLibrary; shared command code lives in templates/Shared. The extension build stages these sources, adds the matching ICO, creates ZIP files, and lets VSSDK build the template catalog into the VSIX. No manually maintained ZIP is needed.

Generated icon originals, transparent PNG exports, ICO exports, and exact prompts live in assets. scripts/export-icons.ps1 recreates the image sizes and ICO containers from the saved originals without generating new artwork.

Reference APIs: [Visual Studio Makefile projects](https://learn.microsoft.com/en-us/cpp/build/reference/creating-a-makefile-project) and [packaging project templates in VSIX](https://learn.microsoft.com/en-us/visualstudio/extensibility/getting-started-with-the-vsix-project-template).
