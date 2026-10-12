# Swift window

The extension provides one dockable Swift window with a left sidebar. Open it with **Extensions > Swift**. All extension-owned labels and messages are English. The window header shows the validated compiler version, language-server state, and package folder.

## Sidebar

| Page | Information and actions |
| --- | --- |
| Toolchain | Selected directory and selection source, compiler version and architecture, inherited SDKROOT; Browse, Validate, Global / Workspace, Apply, Use Global |
| Language Server | Starting / Running / Stopped / Error, active toolchain, last failure, pending restart; Restart and Show Output |
| Package | Package.swift path, actual SwiftPM package name, products and targets; Refresh Package and Open Package.swift |
| Debug | Executable product, program arguments, Stop on entry, per-package F5 preference; Save and Start Debugging |
| Output | Recent file-log history and live language-server/window messages; Clear |

Show Output selects the Output page in the same window. The extension also mirrors new messages into Visual Studio's standard Swift Output pane after its package loads. Clear clears the visible window history; it does not delete the file log.

## Settings and process behavior

Resolution order is workspace `.vs/VisualSwift/settings.json`, global `%LOCALAPPDATA%/VisualSwift/settings.json`, `VISUALSWIFT_TOOLCHAIN`, then PATH. Both executables must exist in the same toolchain bin directory. An invalid saved selection reports an error rather than silently selecting a different compiler.

Apply validates the candidate before saving. Selecting Workspace requires an open Swift package. Use Global clears the workspace override and restores the normal fallback order. A running server keeps its active toolchain until Restart; the window identifies a pending change. Newly generated project-template wrappers read the same settings. Existing generated projects need an updated Invoke-Swift.ps1 wrapper to use persisted selections.

Validation runs `swift --version`, checks SDKROOT, and compares the active solution platform with the compiler target when a solution configuration is available. SDK environment variables are still inherited from the IDE process; installed SDK/toolchain discovery and automatic environment setup are subsequent work.

SwiftPM inspection runs `swift package describe --type json`. Subprocess execution has a 30-second timeout and cancellation; Windows job objects terminate owned child processes. Package.swift remains authoritative. Initial package selection follows the active document or solution, with VISUALSWIFT_WORKSPACE available as an explicit override. Multi-package workspace selection remains subsequent work.

## Validation

Core checks cover settings priority and persistence, invalid overrides, real compiler validation, process output, nonzero exits, and cancellation. The LSP smoke probe covers editor requests and cross-file search/reference behavior. Visual Studio 2026 on Windows ARM64 is the measured IDE environment; Visual Studio 2022 and x64 still need IDE validation.

On 2026-10-12, the single menu entry, all four sidebar pages, compiler/SDK summary, SwiftPM products/targets, and internal Show Output navigation were verified in the experimental IDE profile. Restart terminated SourceKit-LSP pid 30728, started pid 39608, initialized it, and replayed didOpen for the active Swift document. Settings persistence was verified by the core checks; a full IDE close/reopen selection test and additional theme checks remain open.

## Next stages

- SwiftPM Build, Rebuild, Clean, Run, Test, Resolve Dependencies, and Cancel inside this window.
- Installed toolchain discovery and SDK environment selection.
- Project properties, package/target hierarchy, and multiple packages.
- Test Explorer and broader debugger support beyond local SwiftPM executables.

Code search continues through native Visual Studio Code Search and Find All References. See [search validation](SEARCH.md).

The Debug page was added and tested on 2026-10-12. The debugger builds using the selected toolchain and displays standard Visual Studio debugger panes. Debug builds have a five-minute timeout; other Swift queries retain their 30-second timeout. See [debugging setup and validation](DEBUGGING.md).
