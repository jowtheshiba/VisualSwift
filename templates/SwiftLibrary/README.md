# Swift Library

This is a Swift Package Manager library with a public Greeter API and an XCTest example. Package.swift controls the product, targets, and dependencies. Rename the Library and LibraryTests targets and folders when choosing a module name for your package; update the test import as well.

Visual Studio loads the vcxproj as a Makefile project, requiring C++ project support. Build, Rebuild, and Clean delegate to SwiftPM. Debug/Release selects the Swift build configuration. Platform labels do not switch compiler architecture: the selected toolchain determines it.

Install Windows Swift with its SDK dependencies and configure the environment before launching Visual Studio. Set VISUALSWIFT_TOOLCHAIN to a toolchain root or bin directory, or put swift.exe and sourcekit-lsp.exe from the same toolchain on PATH. SDKROOT is inherited.

From this directory:

```powershell
swift build
swift test
.\VisualSwift\Invoke-Swift.ps1 -Command Test -Configuration Release
```

Test Explorer and F5 debugging are not configured. Include newly added source files through Show All Files / Include In Project; SwiftPM reads sources directly regardless of IDE inclusion.

For a solution containing multiple packages, open the desired package as a folder for the current VisualSwift language-server prototype. Alternatively set VISUALSWIFT_WORKSPACE to one package root before launching the IDE.
