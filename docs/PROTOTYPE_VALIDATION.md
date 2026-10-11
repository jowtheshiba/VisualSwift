# Compatibility prototype validation

Validated on 2026-10-11. The milestone 0 editor exit criteria are satisfied in the primary environment. The broader compatibility matrix is still open.

## Tested environment

| Component | Measured version or configuration |
| --- | --- |
| Operating system architecture | Windows ARM64 |
| IDE | Visual Studio 2026 Community 18.10.3 |
| Swift | 6.4, assertions toolchain, aarch64-unknown-windows-msvc |
| VSIX target | .NET Framework 4.7.2 |
| Visual Studio SDK | Microsoft.VisualStudio.SDK 17.14.40265 |
| IDE LSP client SDK | Microsoft.VisualStudio.LanguageServer.Client 17.14.60 |
| Packaging tools | Microsoft.VSSDK.BuildTools 18.9.820 |
| Probe | .NET SDK 10.0.401, Newtonsoft.Json 13.0.3 |
| IDE test profile | VisualSwiftClean, separate from the regular IDE profile |

The manifest permits Visual Studio 17.14 through 18.x on amd64 and arm64. This is a candidate installation range, not a verified support matrix. Visual Studio 2022 and x64 have not received IDE smoke tests.

## Results

| Check | Result and evidence |
| --- | --- |
| VSIX build | Successful Debug build and development-profile deployment |
| Packaging | Extension DLL, registration/settings, LICENSE, and NOTICE included; VS SDK runtime assemblies not bundled |
| Swift compiler | Version queried successfully; samples/LspSmoke built with SwiftPM |
| SourceKit-LSP startup | Actual initialize exchange and initialized callback successful |
| Swift activation | .swift editor activation verified; .swiftinterface registered but not exercised |
| Open Folder | Package sources opened from Solution Explorer; IDE supplied the package root in initialize |
| Completion | IDE Ctrl+Space displayed greeting(name: String) and self; probe asserted the expected member |
| Hover | IDE Quick Info displayed func greeting(name: String) -> String; probe returned hover contents |
| Definition | F12 navigated from the call to the method declaration; probe returned its location |
| Diagnostics | Unsaved String-to-Int error produced red editor underline and error count; probe asserted severity 1 |
| Rename | Probe returned edits for declaration and call; applying a rename through the IDE remains unverified |
| Symbol search | Native Code Search displayed indexed Swift types/members from main.swift and the unopened SearchFixture.swift without a custom Navigate To provider; probe checked declaration URI/position and an unknown query |
| References | After indexing, Shift+F12 displayed declaration and calls in both files; double-clicking the second-file result opened SearchFixture.swift at its call; probe asserted cross-file references |
| Protocol framing | Shared adapter tested with byte-by-byte frames, multiple frames per write, UTF-8 lengths, and a Unicode path containing spaces |
| Server lifecycle | Probe shutdown/exit with stdin closed completed with exit code 0; indexed search sessions otherwise waited for transport EOF; IDE close/reopen activated a new server |
| Syntax presentation | Installed IDE provided Swift coloring; no external grammar was copied |

Temporary editor diagnostic changes were undone without saving. The fixture remains valid Swift source. Machine-local results are stored in artifacts/lsp-smoke.json and development logs in %TEMP%/VisualSwift. Those generated files are excluded from Git.

## Technical decisions

Use a native C# VSIX with ILanguageClient, MEF content-type exports, and standard-input/output SourceKit-LSP transport. This establishes an integration point for later menus and project support without a Node.js runtime in the extension.

The measured IDE advertises dynamic registration and pull diagnostics. With those original capabilities, member completion did not activate and the expected diagnostic was absent. The LspInputStream adapter changes existing dynamicRegistration values to false and removes textDocument.diagnostic before forwarding initialize. SourceKit-LSP then supplies static providers and push diagnostics, and both editor features work. Other capability values are preserved. These adjustments are compatibility behavior for the measured combination and should be revisited when updating either side.

The adapter also supplies an explicit package root when VISUALSWIFT_WORKSPACE is set. Open Folder already supplies a root; a standalone file did not. The prototype supports one package context. Resolving package roots for arbitrary solutions and multiple packages is future work.

Toolchain resolution accepts an explicit bin/root directory or a PATH directory containing both executables. The server inherits the configured Windows SDK environment and prepends the chosen bin directory to PATH. Full environment discovery, architecture validation, persistence, and a toolchain menu belong to the next milestone.

Native Visual Studio MSBuild is used for development-profile deployment. dotnet build is sufficient for generating the VSIX. NuGet packages supply the pinned extension SDK/build tools; a separately installed extension-development workload was not present in this validation environment.

No vscode-swift code or resources were imported. Future ports require a pinned upstream revision and a license/provenance audit. New implementation files contain no explanatory code comments. Package.swift includes the SwiftPM-required swift-tools-version directive as manifest metadata.

## Reproduction

Run scripts/build.ps1, then scripts/test-lsp.ps1 in a shell with the installed Swift SDK environment. The probe builds an actual language-server session using the same input adapter compiled into the extension, checks protocol framing, and records editor-request results and shutdown status.

Deploy using scripts/deploy-prototype.ps1 with the selected vswhere instance ID. Launch that IDE with the matching /RootSuffix and the sample package path, setting VISUALSWIFT_WORKSPACE before launch for a pinned context. Open main.swift, request completion, hover the method, press F12 at the call, and introduce then undo a temporary type mismatch.

## Remaining checks and fallbacks

- Test Visual Studio 2022 and Windows x64 before declaring them supported; narrow the manifest range or split packages if the SDK behavior differs.
- Validate search in solution-owned Swift projects and with incomplete/cold indexes. Current acceptance covers one built SwiftPM package opened as a folder. Code Search result activation has not been separately exercised; reference-result activation and F12 across files were verified.
- Verify rename edits through Visual Studio, .swiftinterface activation, extension unload while the IDE remains open, and recovery after server failure.
- Validate a Swift file owned by a solution/project with a package root different from the solution directory. Until the project wrapper exists, Open Folder with one package and an explicit root override is the supported prototype workflow.
- Test SDK discovery without inherited shell variables, invalid toolchains, and runtime prerequisites during the toolchain milestone.
- Integrate Swift Output, persistent selection, restart commands, native build commands, and project templates in later milestones. These are not part of this VSIX yet.

The result establishes that native Visual Studio language support using the installed SourceKit-LSP is viable. It does not establish production readiness or full project/debugging integration.
