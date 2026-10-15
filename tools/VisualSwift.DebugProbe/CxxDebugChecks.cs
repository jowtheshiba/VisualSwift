using Newtonsoft.Json.Linq;

static class CxxDebugChecks
{
    public static async Task Run(string executable, string program, string workspace, string reportPath)
    {
        var report = new JObject();
        using var adapter = new DapClient(executable);
        try
        {
            await adapter.Request("initialize", new { adapterID = "swift", clientID = "VisualSwift.CxxProbe", linesStartAt1 = true, columnsStartAt1 = true, pathFormat = "path" });
            var swift = Path.GetFullPath(Path.Combine(workspace, "Sources", "App", "main.swift"));
            var cpp = Path.GetFullPath(Path.Combine(workspace, "Sources", "CppMath", "CppMath.cpp"));
            var launch = adapter.Request("launch", new { program = Path.GetFullPath(program), cwd = Path.GetFullPath(workspace), stopOnEntry = false });
            await adapter.Event("initialized");
            report["swiftBreakpoint"] = await adapter.Request("setBreakpoints", new { source = new { path = swift }, breakpoints = new[] { new { line = 4 } } });
            report["cppBreakpoint"] = await adapter.Request("setBreakpoints", new { source = new { path = cpp }, breakpoints = new[] { new { line = 6 } } });
            if ((bool?)report["swiftBreakpoint"]?["breakpoints"]?[0]?["verified"] != true || (bool?)report["cppBreakpoint"]?["breakpoints"]?[0]?["verified"] != true) throw new InvalidOperationException("Mixed-language breakpoints did not bind.");
            await adapter.Request("configurationDone", new { }); await launch;
            var stop = await adapter.Event("stopped"); var thread = (int)stop["threadId"]!;
            var stack = await adapter.Request("stackTrace", new { threadId = thread }); report["swiftStack"] = stack;
            if (!string.Equals((string?)stack["stackFrames"]?[0]?["source"]?["path"], swift, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Debugger did not stop in the Swift caller.");
            await adapter.Request("stepIn", new { threadId = thread });
            stop = await adapter.Event("stopped"); thread = (int)stop["threadId"]!;
            stack = await adapter.Request("stackTrace", new { threadId = thread });
            for (var attempt = 0; attempt < 8 && !string.Equals((string?)stack["stackFrames"]?[0]?["source"]?["path"], cpp, StringComparison.OrdinalIgnoreCase); attempt++)
            {
                await adapter.Request("stepIn", new { threadId = thread }); stop = await adapter.Event("stopped"); thread = (int)stop["threadId"]!;
                stack = await adapter.Request("stackTrace", new { threadId = thread });
            }
            report["cppStack"] = stack;
            if (!string.Equals((string?)stack["stackFrames"]?[0]?["source"]?["path"], cpp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Step Into did not enter C++ source.");
            var frame = (int)stack["stackFrames"]![0]!["id"]!;
            report["cppAmount"] = await adapter.Request("evaluate", new { expression = "amount", frameId = frame, context = "watch" });
            report["cppValue"] = await adapter.Request("evaluate", new { expression = "this->value", frameId = frame, context = "watch" });
            if ((string?)report["cppAmount"]?["result"] != "2" || (string?)report["cppValue"]?["result"] != "40") throw new InvalidOperationException("C++ locals could not be evaluated.");
            await adapter.Request("stepOut", new { threadId = thread }); await adapter.Event("stopped");
            stack = await adapter.Request("stackTrace", new { threadId = thread }); report["afterStepOut"] = stack;
            await adapter.Request("continue", new { threadId = thread }); await adapter.Event("terminated");
            await adapter.Request("disconnect", new { terminateDebuggee = true }); report["passed"] = true;
        }
        catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); Environment.ExitCode = 1; }
        finally { report["messages"] = adapter.Snapshot(); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!); await File.WriteAllTextAsync(reportPath, report.ToString()); Console.WriteLine(report.ToString()); }
    }
}
