# Swift debugging

VisualSwift launches executable SwiftPM products through the selected toolchain's `lldb-dap.exe` and Visual Studio's Debug Adapter Host. Debugging uses standard editor breakpoints, Call Stack, Locals, Watch, Continue, Step Over, and Stop Debugging.

## Launch

1. Open a SwiftPM package using Open Folder, or open a generated Swift project and one of its Swift files.
2. Open **Extensions > Swift** and select **Debug** in the sidebar.
3. Select an executable product. Set arguments as a JSON array of strings, for example `["--verbose", "a file.txt"]`.
4. Optionally select **Stop on entry** and **Use Swift debugger for F5 in this package**.
5. Set an editor breakpoint and select **Start Debugging**. This saves the settings, builds the product, and launches it.

Use **Save** to persist settings without launching. With the F5 preference enabled, F5 builds and launches when no debug session is active. During debugging, F5 continues, F10 steps over, and Shift+F5 stops through Visual Studio's standard commands. Other packages retain their own startup behavior.

Preferences are stored in `.vs/VisualSwift/debug.json`; the generated adapter configuration is `.vs/VisualSwift/launch.json`. Both are local IDE files. Each launch resolves the selected toolchain again. SDK variables are inherited from Visual Studio. The adapter receives the selected toolchain bin directory first in PATH for Swift runtime loading.

## Requirements and symbol format

The selected toolchain must contain `swift.exe`, `sourcekit-lsp.exe`, `lldb-dap.exe`, and `lld-link.exe`, with SDKROOT configured before opening Visual Studio. Visual Studio must include its Debug Adapter Host.

The debug launcher builds with:

```text
swift build -c debug --product PRODUCT -Xswiftc -use-ld=lld -Xlinker /debug:dwarf
```

In the measured Swift 6.4 Windows ARM64 toolchain, the default linker truncates DWARF section names. LLDB sees functions but cannot bind source breakpoints. LLVM's linker with `/debug:dwarf` preserves the required sections. These flags are applied by the debug launcher independently of ordinary template builds. Debug builds have a five-minute timeout; metadata and path queries retain the 30-second timeout. Builds started from the Swift window can be cancelled there. Windows job objects terminate owned build subprocesses.

## Validation

Measured on 2026-10-12 with Visual Studio 2026 Community 18.10.3, Windows ARM64, Swift 6.4, and bundled LLDB 21.1.6:

- Debug lists the actual `DebugSmoke` executable and saves the F5 preference.
- Start Debugging builds and launches in the native Visual Studio debugger.
- A source breakpoint binds and stops on `main.swift` line 5 in `Counter.add`.
- Call Stack displays `Counter.add` and its caller. Locals displays `amount = 1`, `self`, and `before = 41`.
- Watch evaluates `self.value` to `41`; F10 moves to line 6 and changes it to `42`.
- F5 continues to exit code 0 with program output in the standard Debug Output pane.
- F5 from idle launches a second session and reaches the same breakpoint. Shift+F5 stops it.

Run the independent adapter check with:

```powershell
.\scripts\test-debug.ps1
```

It builds `samples/DebugSmoke` with the same linker flags and checks breakpoint binding, stack locations, locals, Swift Watch expressions, stepping, and termination over DAP. The report is `artifacts/debug-smoke/dap.json`. It complements the IDE checks; it does not automate Visual Studio.

## Current limits

The initial scope is local executable products from one SwiftPM package. Attach, remote debugging, debugging Swift tests, library-only launches, interactive stdin/external terminals, conditional breakpoint behavior, and multi-package startup selection have not been validated. Visual Studio 2022 and Windows x64 need separate IDE checks. Test Explorer remains separate work.

The implementation uses Microsoft's [Debug Adapter Host](https://github.com/microsoft/VSDebugAdapterHost) engine registration and launch API. LLDB comes from the installed Swift toolchain and is not redistributed by the extension.
