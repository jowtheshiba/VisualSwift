# VisualSwift development plan

Status: milestone 0 editor prototype exit criteria verified on Visual Studio 2026 / Windows ARM64. At the user's request, initial project templates and SwiftPM Build/Rebuild/Clean wrappers were implemented before the toolchain UI. This is partial milestone 3 work using the built-in Makefile project system; the dedicated project system remains open. See [prototype validation](PROTOTYPE_VALIDATION.md) and [project templates](PROJECT_TEMPLATES.md).

Prepared: 2026-10-11.

Search follow-up: native Code Search displays Swift workspace symbols, including closed files. Cross-file Find All References and F12 navigation were verified on the primary IDE after indexing. Automated acceptance checks live in the LSP probe; see [search validation](SEARCH.md). No additional Navigate To provider is needed for the measured SDK.

## Product requirements

1. Extend Microsoft Visual Studio on Windows with Swift language support.
2. Use SourceKit-LSP from the selected Swift toolchain as the language engine.
3. Provide a Swift menu that visibly identifies the selected toolchain, including when unavailable.
4. Use English for extension-owned UI, messages, settings, and template names.
5. Write new code without comments. Keep architectural explanations in documentation.
6. Integrate with standard Visual Studio editing, output, diagnostics, build, and project workflows where supported.
7. Provide Visual Studio project templates for Swift applications and libraries.
8. License original project code under Apache-2.0.

## Initial scope and assumptions

- Primary target: Visual Studio 2026. The installed validation environment is Windows ARM64; x64 remains a compatibility candidate.
- Compatibility target: Visual Studio 2022 17.14, subject to prototype validation. Do not claim compatibility before testing it.
- Initial project model: one SwiftPM package opened using Open Folder.
- Initial toolchains: locally installed Windows Swift toolchains, with an explicit path override.
- Use the installed Swift 6.4 toolchain as the first validation candidate; its executable and SourceKit-LSP must pass validation before it becomes a supported version.
- Users install Swift and its platform dependencies separately. The extension detects missing prerequisites and provides actionable messages.
- Initial templates: Swift Console Application and Swift Library.
- Remote Mac builds, Xcode projects, SwiftUI previews, WSL, and multi-root workspaces are deferred.
- Debugging and Test Explorer are subsequent milestones; running swift test is part of the initial release.

## Architecture

Use a native C# VSIX extension with a Visual Studio-independent core. Avoid a Node.js runtime dependency for the initial implementation.

| Component | Responsibility |
| --- | --- |
| VisualSwift.Core | Toolchain discovery and validation, process execution, SwiftPM metadata, settings models |
| VisualSwift.VisualStudio | Extension lifecycle, menus, tool windows, options, workspace tracking, Output and Error List integration |
| Language server adapter | Register Swift documents, connect the IDE LSP client to SourceKit-LSP, configure workspace roots, manage restart and shutdown |
| Project integration | Load Swift projects into solutions and bridge standard build commands to SwiftPM |
| Templates | Package Visual Studio templates and initialize SwiftPM projects |

The prototype selects VSSDK with ILanguageClient and a .NET Framework 4.7.2 VSIX. A stream adapter requests static registration and push diagnostics for the measured SourceKit-LSP / IDE combination. Keep this integration path unless later compatibility evidence requires revisiting it.

The selected toolchain must supply swift, sourcekit-lsp, and the environment used for all build/run/test operations. Do not combine executables from different toolchains through incidental PATH resolution.

Package.swift remains authoritative for dependencies, targets, and products. A future Visual Studio project wrapper contains IDE-specific settings and delegates compilation to SwiftPM rather than duplicating the package model.

## Reuse of vscode-swift

Audit a tagged release of swiftlang/vscode-swift and record its exact commit before importing or porting material. The main branch is a reference, not an unpinned dependency.

| Area | Strategy |
| --- | --- |
| Toolchain discovery and environment setup | Port relevant Windows behavior to C# |
| SwiftPM command construction and metadata | Adapt algorithms and data models |
| SourceKit-LSP lifecycle and configuration | Reuse protocol knowledge and reproduce compatible behavior |
| Snippets and editor assets | Convert usable resources and validate against Visual Studio |
| VS Code menus, tasks, settings, tree views | Replace with Visual Studio APIs |
| Testing and debug integration | Reuse Swift-side behavior; implement IDE-specific adapters |
| Project templates and solution loading | Implement for Visual Studio |

Review licenses for each imported resource and dependency. Keep provenance, license texts, relevant NOTICE entries, and change attribution with the distributed artifacts. Preserve required notices in reused files; if a file would conflict with the no-comments requirement, resolve its treatment before importing it. Do not copy branding assets without checking their terms.

## Milestone 0: compatibility prototype and technical decisions

Tasks:

- Confirm Visual Studio extension development prerequisites and a reproducible VSIX build.
- Validate swift --version, SourceKit-LSP startup, and a small SwiftPM package build on the local machine.
- Register .swift and .swiftinterface documents and connect SourceKit-LSP over standard input/output.
- Verify initialize capabilities, Swift language identifiers, workspace roots, Windows file URIs, and diagnostic flow.
- Exercise Open Folder activation, reopen, server shutdown, and extension unload.
- Test completion, hover, definition, references, and rename; document unsupported requests and server extensions.
- Check activation and package context when a file belongs to a project in a solution.
- Select SDK/API versions, target frameworks, packaging strategy, and supported IDE range from measured results.

Exit criteria: a development VSIX provides completion, hover, definition, and diagnostics for a real SwiftPM package in the primary IDE. Compatibility results and architectural decisions are recorded, with a concrete fallback for any blocked feature.

## Milestone 1: toolchains and extension shell

Tasks:

- Discover supported installed toolchains and allow adding a directory manually.
- Validate executable paths, version, architecture, SDK availability, and runtime environment with bounded subprocess calls.
- Add a Swift menu with Current Toolchain, Select Toolchain..., Restart Language Server, and Show Swift Output.
- Display version and path in a Swift Toolchains window; distinguish selected configuration from active server state.
- Persist a global default and a workspace override without committing machine-specific paths by default.
- Use one settings snapshot for LSP and SwiftPM processes.
- Serialize toolchain changes, cancel dependent operations where needed, and restart the server without leaving orphan processes.

Exit criteria: selection survives IDE restart; switching changes both language server and compiler; invalid selections produce an English message and a usable recovery action.

## Milestone 2: editor and SwiftPM workflow

Tasks:

- Add licensed Swift syntax grammar and editor configuration, checking their actual source rather than assuming vscode-swift owns them.
- Surface supported LSP completion, hover, navigation, diagnostics, rename, and code actions.
- Evaluate formatting against the selected server capabilities; use a validated formatter integration if required.
- Read SwiftPM metadata to identify targets, products, dependencies, and executable choices.
- Add Build Package, Clean Package, Run Product, Test Package, and Resolve Dependencies commands.
- Route process output to a Swift Output pane and map supported compiler diagnostics into Error List without duplicating LSP diagnostics.
- Provide cancellation, nonzero exit reporting, working-directory control, safe argument passing, and interactive console support for executables.
- Coordinate builds and indexing so cross-module navigation can recover after a build.

Exit criteria: a package can be edited, built, run, and tested from Visual Studio using the selected toolchain. Build errors navigate to the correct file and line; cancellation leaves no active child process.

This milestone is the editor MVP. It is not the full requested project/template release.

## Milestone 3: projects and templates

Tasks:

- Prototype a minimal project wrapper and choose a supported project-system approach before implementing a full hierarchy.
- Make the Swift package loadable into .sln with source files, package targets, and dependencies represented in Solution Explorer.
- Bridge standard Build/Rebuild/Clean commands and configuration selection to SwiftPM.
- Add project properties for toolchain selection, build configuration, executable product, and arguments.
- Keep the LSP workspace rooted at the package, including when the solution is elsewhere.
- Package Swift Console Application and Swift Library templates in the VSIX.
- Add Swift source file item templates if supported by the chosen project model.
- Generate Package.swift using the selected toolchain or validated template content, including SwiftPM-required manifest metadata.
- Ensure package edits and added files update the IDE model without introducing a second dependency definition.

Exit criteria: File > New > Project creates both project types; each opens in a solution, builds through standard IDE commands, and retains equivalent command-line SwiftPM behavior. Existing packages can be opened or imported without rewriting their manifest.

This is the first release satisfying the requested project and template scope.

## Milestone 4: tests and debugging

Tasks:

- Implement test discovery and execution adapters for supported Swift Testing and XCTest versions.
- Map test results and source locations into Test Explorer.
- Validate available LLDB/DAP tooling and the Visual Studio debug adapter integration path on Windows.
- Support breakpoints, stepping, stack traces, locals, and termination for a console application before expanding scope.
- Integrate startup product selection and F5 behavior after debugging validation.

Exit criteria: supported tests can be discovered and run individually; a Swift executable can be debugged in Visual Studio with reliable breakpoints and stepping. Unsupported combinations are documented explicitly.

## Milestone 5: distribution and maintenance

Tasks:

- Build release VSIX artifacts in a Windows environment with explicit SDK versions.
- Validate installation, upgrade, uninstall, and settings migration.
- Include licensing, notices, prerequisite instructions, and the tested IDE/toolchain matrix in the distribution.
- Add release notes and troubleshooting for toolchain discovery, server initialization, and package indexing.
- Publish only after the release artifact has passed installation and smoke testing.

Exit criteria: a clean supported environment can install the VSIX, select a toolchain, create a template project, build it, and receive editor diagnostics using documented steps.

## Verification strategy

- Unit tests for toolchain selection precedence, path/environment construction, SwiftPM metadata parsing, and process cancellation.
- Integration tests with real SwiftPM packages covering an executable, a library, and a local dependency.
- Fixtures for invalid toolchain paths, failed builds, paths containing spaces and non-ASCII characters, and package roots differing from solution roots.
- IDE smoke tests for extension activation, LSP features, switch/restart behavior, templates, build navigation, and solution reload.
- Re-run the relevant matrix when changing SDK versions, protocol handling, toolchain support, or project integration.
- Add debugging and test discovery coverage when those adapters exist; do not imply those features are verified in the editor MVP.

## Risks and decision gates

| Risk | Gate or mitigation |
| --- | --- |
| LSP client and SourceKit-LSP capability mismatch | Verify actual requests and results in milestone 0; do not advertise untested features |
| Incomplete package context in solution mode | Validate roots and initialization before committing to the project wrapper |
| Swift indexing differs across versions | Validate after build and document supported indexing behavior |
| Windows toolchain prerequisites and environment vary | Validate prerequisites and isolate each selected toolchain's process environment |
| SDK differences between VS 2022 and VS 2026 | Test both; narrow the support range or use separate packages if required |
| Native Swift debugging requires additional integration | Keep a separate milestone with a working Windows prototype as its first gate |
| Imported code is tightly coupled to VS Code | Port bounded algorithms into the independent core rather than recreating the VS Code extension host |

No calendar estimate is committed before milestone 0 establishes compatibility and the project-system approach. Deliver milestones sequentially, revising later implementation details when a decision gate produces new evidence.

## References

- [SourceKit-LSP](https://github.com/swiftlang/sourcekit-lsp)
- [Swift for Visual Studio Code](https://github.com/swiftlang/vscode-swift)
- [vscode-swift license](https://github.com/swiftlang/vscode-swift/blob/main/LICENSE)
- [Visual Studio LSP integration](https://learn.microsoft.com/en-us/visualstudio/extensibility/adding-an-lsp-extension)
- [VisualStudio.Extensibility language server provider](https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio.extensibility/language-server-provider/language-server-provider?view=vs-2022)
- [Visual Studio project and item templates](https://learn.microsoft.com/en-us/visualstudio/extensibility/creating-custom-project-and-item-templates?view=vs-2019)
- [Swift installation on Windows](https://www.swift.org/install/windows/)
- [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0)
