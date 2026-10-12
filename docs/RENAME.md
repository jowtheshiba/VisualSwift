# Swift Rename

Place the caret on a Swift symbol and use **Rename...** in the editor context menu or **Ctrl+R, Ctrl+R** with the default Visual Studio keyboard mapping. Enter the new name and press Enter. Visual Studio calls SourceKit-LSP's `textDocument/prepareRename` and `textDocument/rename` and applies the returned workspace edits through its native language-client integration. No separate Swift rename dialog or textual replacement engine is needed.

For methods, SourceKit-LSP can present the complete Swift name, including argument labels. For example, change `greeting(name:)` to `welcome(name:)` to preserve the label. Use the name format supplied by the server. **Ctrl+Z** undoes the applied operation.

Allow background indexing to finish. VisualSwift enables SourceKit-LSP background indexing through initialization options and watches files inside the active package independently of Visual Studio's dynamic watcher registration. Create, save, delete, and rename events are batched and forwarded as `workspace/didChangeWatchedFiles`. Directory moves are reconciled with a source-file scan. Pending events are flushed before native Prepare Rename and Rename requests. Generated `.build`, `.git`, and `.vs` contents are excluded from notifications.

Adding a source file no longer requires restarting the language server. Cross-file references still depend on the selected toolchain, package configuration, and asynchronous index. Workspace SourceKit-LSP configuration can override background indexing. A cold index or compilation errors can produce incomplete edits; review affected declarations and calls before continuing. VisualSwift does not block interactive requests using the server's global synchronization extension.

## Validation

On Visual Studio 2026 ARM64 with Swift 6.4, the native Rename command renamed a method declaration and calls in two files, including an initially unopened file. A class declaration and constructor use were also changed through the native command. A local variable rename changed its declaration and use while preserving a same-named local in another function and matching string content. Undo restored the operations. A class rename during the first IDE session missed a newly added unopened fixture; the indexed probe subsequently returned all three expected edits across both files. This demonstrates why a current index is required.

`scripts/test-lsp.ps1` additionally validates six indexed scenarios: class, struct, overloaded method, property, local variable, and global variable. It checks `prepareRename`, edit locations and file counts, applies edits to artifact copies, and type-checks every resulting package source set with the selected Swift compiler. Assertions ensure that an unrelated overload, a shadowed local variable, and matching text inside a string remain unchanged.

The same script tests the production file watcher with a real running SourceKit-LSP session: it creates an unopened source file, adds another reference, moves the file, and deletes it. Each phase verifies the expected Rename edit count and target URI without rebuilding or restarting the server. Only this automated test waits for index synchronization; the native editor uses background updates. Results are recorded under `renameFileChanges` in `artifacts/lsp-smoke.json`.

After installing the watcher fix, the IDE test added a new Swift source file while the server was running, then invoked native Rename on `DeliveryService`. Visual Studio applied all five symbol edits across three files, including the new unopened file and the previously missed fixture. Ctrl+Z restored all edits. No server restart, explicit index synchronization, or additional package build was used in that IDE test.

The checked workflow covers one SwiftPM package. Renaming across multiple package roots, generated macro declarations, external dependencies, and a Roslyn-style preview UI have not been validated. Changes are limited to edits returned by SourceKit-LSP.
