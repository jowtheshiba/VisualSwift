# SwiftPM tests in Visual Studio Test Explorer

The VSIX contains `VisualSwift.TestAdapter.dll`, a VSTest discoverer/executor, and a MEF test-container discoverer for the active SwiftPM package. The package's `Package.swift` is the test source. Open the package or its generated Visual Studio project, then open **Test > Test Explorer**. A Swift source file or **Extensions > Swift** activates the package context if necessary.

Discovery builds the test products and calls `swift test list` separately for XCTest and Swift Testing. Tests have their SwiftPM identifiers, a Framework trait, a Package trait, and a source location when a matching declaration exists under the conventional `Tests` directory. Test discovery comes from the toolchain; source scanning only supplies the optional editor navigation location.

Use the standard **Run All** or **Run Selected** commands. The adapter rebuilds the package's tests once before execution, then runs selected tests individually with `--skip-build` and an anchored filter. XCTest and Swift Testing run separately so their results cannot overwrite each other. A parameterized Swift Testing test is one Test Explorer item; its argument cases run together. Outcome, duration, assertion details, stdout/stderr and an XML attachment are sent to the standard test platform. Missing results and stale test identifiers fail rather than being reported as passed.

SwiftPM's XCTest parallel XML does not encode XCTSkip. The adapter requests verbose output and recognizes the selected test's explicit skipped status. Swift Testing reports skipped tests in XML. The smoke package includes intentional failed and skipped tests to verify both paths.

Test execution uses the package's selected toolchain and inherited Swift SDK environment. Cancellation terminates the owned Swift process tree through a Windows job object. Commands time out after ten minutes. XML artifacts are kept in `<package>\.vs\VisualSwift\TestResults`. Discovery updates after source and manifest changes or a toolchain change in the Swift window. `.build`, `.vs`, and `.git` changes do not trigger recursive rediscovery.

Current scope is one active package, Debug test builds, and test execution. Test debugging, coverage, custom discovery paths, argument cases as separate Explorer items, multi-package discovery, and Release test configuration remain subsequent work. Source navigation is best effort when duplicate method names exist within one test target. Swift Testing support requires a toolchain that provides it; Swift 6.4 is the measured version.

## Reproduction

Build with `scripts/build.ps1`. It locates the installed Visual Studio TestWindow interfaces using vswhere; callers building the csproj directly must provide `VisualStudioTestWindowPath`. The VS SDK runtime and test platform runtime remain supplied by Visual Studio. The test adapter bundles its Newtonsoft.Json dependency.

Run `scripts/test-tests.ps1 -ToolchainBin <swift-bin>`. It copies `samples/TestSmoke` to a path with spaces, uses a workspace toolchain override, and runs the adapter through the installed `vstest.console.exe`. It verifies seven discovered tests, three passed, two failed, two skipped, assertion details, a parameterized test, and Run Selected. The two failures are intentional, so the complete fixture run must return exit code 1. The script itself succeeds only after the expected results match. Reports and TRX files are under `artifacts/test-adapter-checks`.
