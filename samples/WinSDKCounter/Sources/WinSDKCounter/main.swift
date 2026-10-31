import WinSDK

// Win32's W functions accept null-terminated UTF-16 strings.
func withWideString<T>(_ string: String, _ body: (LPCWSTR) throws -> T) rethrows -> T {
    try (Array(string.utf16) + [0]).withUnsafeBufferPointer { buffer in
        try body(buffer.baseAddress!)
    }
}

let buttonID = 1

// A C callback cannot capture Swift variables. Store this window's counter
// in GWLP_USERDATA instead; all access happens on the window's UI thread.
func windowProc(_ window: HWND?, _ message: UINT, _ wParam: WPARAM, _ lParam: LPARAM) -> LRESULT {
    switch message {
    case UINT(WM_CREATE):
        let button = withWideString("BUTTON") { buttonClass in
            withWideString("Click me (+1)") { title in
                CreateWindowExW(
                    0, buttonClass, title,
                    DWORD(WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON),
                    30, 35, 260, 50, window,
                    HMENU(bitPattern: buttonID), GetModuleHandleW(nil), nil
                )
            }
        }
        guard button != nil else { return -1 }
        return 0

    case UINT(WM_COMMAND):
        let controlID = Int(wParam & 0xFFFF)
        let notification = UINT((wParam >> 16) & 0xFFFF)
        if controlID == buttonID && notification == UINT(BN_CLICKED) {
            let count = GetWindowLongPtrW(window, GWLP_USERDATA) + 1
            SetWindowLongPtrW(window, GWLP_USERDATA, count)
            _ = withWideString("Clicks: \(count) — Swift + WinSDK") {
                SetWindowTextW(window, $0)
            }
            return 0
        }

    case UINT(WM_DESTROY):
        PostQuitMessage(0)
        return 0

    default:
        break
    }
    return DefWindowProcW(window, message, wParam, lParam)
}

func run() -> UINT {
    let instance = GetModuleHandleW(nil)

    // Keep the class-name buffer alive through registration and window creation.
    return withWideString("VisualSwift.WinSDKCounter") { className in
        var windowClass = WNDCLASSEXW()
        windowClass.cbSize = UINT(MemoryLayout<WNDCLASSEXW>.size)
        windowClass.lpfnWndProc = windowProc
        windowClass.hInstance = instance
        windowClass.hCursor = LoadCursorW(nil, LPCWSTR(bitPattern: 32512)) // IDC_ARROW
        windowClass.hbrBackground = HBRUSH(bitPattern: Int(COLOR_WINDOW) + 1)
        windowClass.lpszClassName = className

        guard RegisterClassExW(&windowClass) != 0 else {
            print("RegisterClassExW failed: \(GetLastError())")
            return 1
        }
        defer { UnregisterClassW(className, instance) }

        let window = withWideString("Clicks: 0 — Swift + WinSDK") { title in
            CreateWindowExW(
                0, className, title,
                DWORD(WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX),
                CW_USEDEFAULT, CW_USEDEFAULT, 340, 170,
                nil, nil, instance, nil
            )
        }
        guard let window else {
            print("CreateWindowExW failed: \(GetLastError())")
            return 1
        }

        ShowWindow(window, SW_SHOW)
        UpdateWindow(window)

        var message = MSG()
        while GetMessageW(&message, nil, 0, 0) {
            // Enable Tab / Enter navigation for the button.
            if !IsDialogMessageW(window, &message) {
                TranslateMessage(&message)
                DispatchMessageW(&message)
            }
        }
        return UINT(truncatingIfNeeded: message.wParam)
    }
}

ExitProcess(run())
