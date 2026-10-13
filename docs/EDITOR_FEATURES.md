# Signature Help, Inlay Hints and type navigation

VisualSwift uses Visual Studio's built-in LSP editor integration and the selected SourceKit-LSP toolchain for these features.

## Commands

Signature Help opens while entering a call at the server's trigger characters, or with **Ctrl+Shift+Space**. It displays the available signatures and highlights the active parameter. **Go To Implementation** and **Go To Type Definition** use the standard editor commands. Implementation navigation requires the package index; allow indexing to finish after a build or source-file change.

## Inlay Hints

Open **Extensions > Swift > Language Server**. **Show inlay hints**, **Inferred types**, and **Parameter names** control the corresponding hints. Save globally or for the current workspace with **Apply Hints**, or restore global inheritance with **Use Global Hints**. Applying settings restarts the language server to refresh open editors. If no server is active, settings take effect on its next activation.

Settings share the existing JSON files: `%LOCALAPPDATA%\VisualSwift\settings.json` and `<package>\.vs\VisualSwift\settings.json`. The `inlayHints` object contains `enabled`, `types`, and `parameters` booleans. All default to `true`. Workspace values override global values. Toolchain and hint settings are preserved independently.

The extension filters LSP hint kinds 1 (type) and 2 (parameter). It does not invent parameter names from source text. The installed Swift 6.4 SourceKit-LSP produces inferred type hints but no parameter hints, including for an unlabeled argument with an internal parameter name. The parameter setting controls hints when a future or alternate toolchain supplies them. Visual Studio's own inline-hint display options can also affect visibility.

## Validation

`scripts/test-lsp.ps1` checks an actual two-parameter method signature and its active argument, an inferred variable's type declaration, a protocol's conforming implementation, and inferred type hints. Results are in `artifacts/lsp-smoke.json` under `editorFeatures`. The core checks cover default settings, global/workspace persistence and fallback, independent toolchain settings, and hint-kind filtering.

Measured toolchain: Swift 6.4 on Windows ARM64. Visual Studio 2022 and x64 still require IDE validation.
