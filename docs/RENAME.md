# Swift Rename

Place the caret on a Swift symbol and use **Rename...** in the editor context menu or **Ctrl+R, Ctrl+R** with the default Visual Studio keyboard mapping. Enter the new name and press Enter. Visual Studio calls SourceKit-LSP's `textDocument/prepareRename` and `textDocument/rename` and applies the returned workspace edits through its native language-client integration. No separate Swift rename dialog or textual replacement engine is needed.

For methods, SourceKit-LSP can present the complete Swift name, including argument labels. For example, change `greeting(name:)` to `welcome(name:)` to preserve the label. Use the name format supplied by the server. **Ctrl+Z** undoes the applied operation.

Build the package first and allow indexing to finish. Cross-file references depend on the selected toolchain, package build configuration, and index. A cold or stale index can produce incomplete edits; review the affected declarations and calls before continuing. Restarting the language server after adding source files refreshes its package context.

## Validation

On Visual Studio 2026 ARM64 with Swift 6.4, the native Rename command renamed a method declaration and calls in two files, including an initially unopened file. A class declaration and constructor use were also changed through the native command. A local variable rename changed its declaration and use while preserving a same-named local in another function and matching string content. Undo restored the operations. A class rename during the first IDE session missed a newly added unopened fixture; the indexed probe subsequently returned all three expected edits across both files. This demonstrates why a current index is required.

`scripts/test-lsp.ps1` additionally validates six indexed scenarios: class, struct, overloaded method, property, local variable, and global variable. It checks `prepareRename`, edit locations and file counts, applies edits to artifact copies, and type-checks every resulting package source set with the selected Swift compiler. Assertions ensure that an unrelated overload, a shadowed local variable, and matching text inside a string remain unchanged.

The checked workflow covers one SwiftPM package. Renaming across multiple package roots, generated macro declarations, external dependencies, and a Roslyn-style preview UI have not been validated. Changes are limited to edits returned by SourceKit-LSP.
