# Swift formatting

VisualSwift uses Visual Studio's native LSP formatting integration with SourceKit-LSP and `swift-format` from the active Swift toolchain. It formats the current editor buffer, including unsaved edits, and applies text edits through the editor's Undo history.

| Command | Default shortcut | Scope |
| --- | --- | --- |
| Edit > Advanced > Format Document | Ctrl+K, Ctrl+D | Current document |
| Edit > Advanced > Format Selection | Ctrl+K, Ctrl+F | Selected Swift range |
| Undo | Ctrl+Z | Revert the formatting operation |

Open a Swift file and wait for the language server to activate. Select a toolchain in Extensions > Swift > Toolchain and restart the server after changing it. The Language Server page shows the formatter path and the command shortcuts. The toolchain must contain a working `swift-format.exe`; older language servers may not support range formatting.

Formatting does not save the document. Save explicitly when ready. VisualSwift adds no Format on Save setting or save-triggered formatting handler. Visual Studio may independently request formatting during editor actions such as paste; that is separate from saving.

## Configuration

The nearest `.swift-format` file in the document's directory or an ancestor takes priority. If none exists, SourceKit-LSP maps the native formatting request's `tabSize` and `insertSpaces` to the formatter. Keyboard mappings and editor indentation settings can be customized in Visual Studio.

For a package using four spaces and a 100-character line limit, create `.swift-format` beside `Package.swift`:

```json
{
  "version": 1,
  "indentation": { "spaces": 4 },
  "lineLength": 100
}
```

Nested configurations can override the package configuration. The [official swift-format configuration reference](https://github.com/swiftlang/swift-format/blob/main/Documentation/Configuration.md) describes available rules. No package configuration is generated or overwritten automatically.

Selection formatting follows swift-format's range behavior; it is not identical to applying whole-document transformations to a substring. Some transformations, such as splitting a single-line function body, occur only during document formatting. Select complete syntax constructs for predictable results.

## Errors and validation

Invalid configuration JSON and unparsable Swift produce a server formatting error instead of a usable edit response. The extension records native formatting failures in Swift Output. Repair the configuration or syntax and run the command again. Inspect toolchain selection and the formatter path if the executable is unavailable. Missing-executable behavior is not separately simulated against the installed toolchain.

Measured on Visual Studio 2026 ARM64 with Swift 6.4: both keyboard commands reached SourceKit-LSP, document formatting expanded a function body, and selection formatting corrected only the selected function's indentation. Ctrl+Z restored each operation; the editor stayed unsaved and the disk file retained its original contents.

`scripts/test-lsp.ps1` records `formattingChecks` in `artifacts/lsp-smoke.json`. It verifies unsaved-buffer formatting, selection isolation, UTF-16 edits with Unicode text and a Unicode path, tabs/spaces, parent and nearest configuration precedence, idempotence, CRLF input, invalid configuration, invalid Swift, unchanged disk contents, and modern Swift syntax. The formatted modern Swift fixture was additionally type-checked with Swift 6.4. The CRLF check validates edit application; it does not promise byte-for-byte preservation of line endings.
