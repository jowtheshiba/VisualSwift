# Swift + WinSDK counter

A native Windows window with a button. Each click increments the counter in
the window title. The example uses WinSDK directly, without a UI framework.

From the repository root, with the Windows Swift toolchain and SDK configured:

```powershell
swift run --package-path samples/WinSDKCounter -Xswiftc -use-ld=lld
```

You can also open this folder in Visual Studio with VisualSwift installed.
Close the window to stop the application.

`main.swift` demonstrates UTF-16 strings, registering a window class,
creating a child button, handling `WM_COMMAND`, keeping per-window state in
`GWLP_USERDATA`, and running the Windows message loop.

Win32 reference: [Using Windows](https://learn.microsoft.com/en-us/windows/win32/winmsg/using-windows).
