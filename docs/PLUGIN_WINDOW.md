# Swift tool window proposal

Status: proposed interface for the next implementation step. All extension-owned labels and messages are English.

## Entry points and presentation

Provide a dockable Swift tool window using Visual Studio fonts, colors, focus behavior, and keyboard navigation. Open it from the Swift menu. The menu also displays the current toolchain and exposes Select Toolchain, Restart Language Server, and Show Swift Output.

The compact summary always shows the active toolchain version, active package name, and language-server state. Distinguish the saved selection from the toolchain used by the currently running server when a restart is pending.

## Initial window

| Section | Visible information | Actions |
| --- | --- | --- |
| Toolchain | Version, bin path, architecture, SDK path, validation result, selection scope | Select Toolchain, Add Toolchain, Validate |
| Language Server | Running / Starting / Stopped / Error, active toolchain, last failure | Restart, Show Log |
| Package | Package.swift path, detected targets/products, selected executable, Debug / Release | Refresh Package, Open Package.swift |

Persist a global default and an optional workspace override. Label the source of the selection as Global or Workspace. Validation and package inspection run asynchronously with cancellation so the IDE stays responsive.

An unavailable toolchain or SDK appears inline with the specific failed check and a recovery action. Validation also checks whether the active Visual Studio solution platform matches the Swift toolchain architecture, because a mismatched native linker environment prevents package manifest compilation. Selecting a new toolchain validates it before starting dependent operations. Restarting replaces the previous server and must not leave a child process running.

## Command integration

After the shared process runner is connected, add Build, Rebuild, Clean, Run, Test, Resolve Dependencies, and Cancel. Run uses the selected executable product; library-only packages do not offer it. Configuration and toolchain are captured when the operation starts and are shown with its result.

Use the standard Swift Output pane for streaming output. Keep a short current-operation summary in the window: action, progress state, elapsed time, exit code, and the latest error. Include Show Output and Copy Diagnostics actions. Prevent conflicting build/clean/test operations for the same package and provide a cancellation result distinct from failure.

Project-template Makefile commands and window commands should converge on the same toolchain/environment resolution rather than growing independent selection rules. Package.swift remains authoritative for package contents.

## Subsequent features

- Installed toolchain discovery and updateable validation results.
- Package dependency and target hierarchy tied to Solution Explorer.
- Project properties and workspace-root selection for multi-package solutions.
- Test Explorer and debugger integration once the corresponding adapters are validated.

## First delivery criteria

- Opening the window immediately identifies the selected toolchain and current package.
- Selection survives IDE restart and clearly distinguishes global and workspace settings.
- An invalid path or missing SDK produces an English inline explanation with an actionable selection/validation command.
- Restart switches the language server to the selected toolchain and reports startup failure without blocking the editor.
- Package refresh shows real SwiftPM products and targets without duplicating the package model.
- Theme changes and keyboard navigation work using normal Visual Studio UI conventions.
