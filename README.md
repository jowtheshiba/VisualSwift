# VisualSwift

Planned Swift language support for Microsoft Visual Studio on Windows, powered by SourceKit-LSP and Swift Package Manager.

The repository currently contains licensing and the development plan. No extension has been implemented yet.

## Intended features

- Swift editing with completion, navigation, diagnostics, and syntax highlighting.
- An English-language Swift menu showing the selected toolchain.
- Toolchain discovery, selection, validation, and language server restart.
- Swift Package Manager build, run, and test commands integrated with Visual Studio.
- Swift console application and library project templates.
- Progressive integration with Solution Explorer, project properties, Test Explorer, and debugging.

Visual Studio 2026 is the proposed primary target. Visual Studio 2022 compatibility will be evaluated during the initial prototype.

See [the development plan](docs/DEVELOPMENT_PLAN.md) for architecture, delivery stages, and acceptance criteria.

## License

Copyright 2026 jowtheshiba.

Licensed under the [Apache License, Version 2.0](LICENSE). See [NOTICE](NOTICE).
