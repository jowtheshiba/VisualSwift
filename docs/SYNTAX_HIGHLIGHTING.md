# Swift syntax highlighting

The audit on 2026-10-12 targets the latest stable release, [Swift 6.4](https://www.swift.org/blog/swift-6.4-released/). The [Windows installation page](https://www.swift.org/install/windows/) supplies Swift 6.4.0. Development snapshots are separate from this compatibility target.

VisualSwift now packages a Swift TextMate grammar instead of relying exclusively on the grammar installed with Visual Studio. Visual Studio loads it through `TextMate\Repositories`; SourceKit-LSP supplies additional semantic classification when available. Colors follow the active IDE theme.

## Source and adaptations

The grammar comes from [jtbandes/swift-tmlanguage](https://github.com/jtbandes/swift-tmlanguage/tree/88500a474365c5bda55588258cb585a1f275529e), the source also used by VS Code's built-in Swift grammar. It is pinned to commit `88500a474365c5bda55588258cb585a1f275529e`, with original JSON SHA-256 `6b517f8403c77452978dddfd0d1d19a458fe1bf1055a9217f5e16848c19a0986`. Its MIT license is preserved alongside the grammar and acknowledged in NOTICE. Original VisualSwift code remains Apache 2.0.

Local adaptations register `.swiftinterface`, remove explanatory grammar comments, classify the error type within typed `throws(...)`, and recognize Swift 6.4 module selectors such as `Swift::Int`. The update script checks the pinned source hash before applying these adaptations.

## Coverage and limits

The checks cover actors, async/await, `nonisolated(nonsending)`, ownership modifiers, `~Copyable`, parameter packs, macros, attributes including `@concurrent` and `@diagnose`, typed throws, optional `any`/`some`, module selectors, raw and multiline strings, nested interpolation, regex literals, numeric literals, escaped identifiers, nested block comments, and conditional compilation.

TextMate classification is lexical and heuristic. It does not validate the semantics of every language construct. Raw strings with two or more hash delimiters are recognized as strings, but their interpolation currently remains in string scope rather than receiving nested Swift lexical scopes. This inherited upstream limitation is documented and tested; complete Swift 6.4 coverage is not claimed.

`samples/LspSmoke/Sources/LspSmoke/SyntaxFixture.swift` is a compiler-checked Swift 6.4 example. The grammar suite contains nine cases and 45 scope assertions, using the actual TextMate tokenizer and Oniguruma regular expressions. The suite also checks that multiline scopes end correctly.

## Reproduction

Node.js is required only for grammar development, not for the VSIX runtime:

```powershell
npm ci --prefix tools/VisualSwift.GrammarChecks --ignore-scripts --no-audit --no-fund
npm test --prefix tools/VisualSwift.GrammarChecks
.\scripts\test-lsp.ps1
```

The tokenizer writes `artifacts/grammar-checks.json`. To regenerate the pinned adapted grammar, run `node tools/VisualSwift.GrammarChecks/adapt.mjs`, then rerun the checks and build the VSIX.

The updated VSIX was deployed to the VisualSwiftTemplatesWizard development profile in Visual Studio 2026 ARM64. The compiler-checked fixture was opened in the IDE and its actors, ownership modifiers, packs, typed throws, module selector, strings, regex literals, and numeric literals were visually inspected. This confirms the editor smoke test on that environment; Visual Studio 2022 and other architectures remain separate compatibility checks.
