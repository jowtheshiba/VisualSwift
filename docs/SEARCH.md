# Swift code search

Swift symbol search uses Visual Studio's existing LSP Code Search integration and SourceKit-LSP's workspace/symbol request. A second Navigate To provider is unnecessary: the measured IDE already collects these results and an additional provider produces duplicate entries.

## Usage

Open one SwiftPM package as a folder, then open any Swift file to activate the language client. Use Edit > Go To > Go To All, normally Ctrl+comma. Search for a type or member name, select the result, and press Enter to navigate to its declaration. The standard search interface supplies symbol filters and keyboard navigation. Use F12 for Go To Definition and Shift+F12 for Find All References.

The server index supplies results from closed files. Newly created or changed files may not appear until indexing catches up. Build the SwiftPM package and allow indexing to complete when checking references. The current client handles one workspace; unrelated packages in the same solution are outside the validated scope.

## Automated validation

scripts/test-lsp.ps1 builds samples/LspSmoke and exercises a real SourceKit-LSP session. It verifies workspace symbol search, an unknown query returning no results, and declaration URI and UTF-16 position. SearchFixture.swift remains unopened in the protocol test and contains ClosedFileGreeter, proving search across closed files. Its call to Greeter.greeting also verifies references between two files. Index-dependent checks retry with a bounded wait.

The probe closes server stdin after shutdown/exit before waiting for process termination. This avoids the observed Swift 6.4 server waiting for transport EOF after indexed searches.

IDE acceptance is recorded in PROTOTYPE_VALIDATION.md. Visual Studio 2022, Windows x64, multi-package search, and an empty or invalid SDK environment remain unverified.

API reference: [Visual Studio LSP extensions](https://learn.microsoft.com/en-us/visualstudio/extensibility/adding-an-lsp-extension).
