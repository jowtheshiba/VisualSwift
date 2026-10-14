using System.Text.Json;
using System.Text.Json.Nodes;

static class CodeActionChecks
{
    public static async Task Run(string server, string workspace, string reportPath)
    {
        await CodeActionBridgeChecks.Run();
        await ProtocolChecks.Run();
        workspace = Path.GetFullPath(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "Sources", "App"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "Package.swift"), "// swift-tools-version: 6.0\nimport PackageDescription\nlet package = Package(name: \"CodeActionSmoke\", targets: [.executableTarget(name: \"App\")])\n");
        var path = Path.Combine(workspace, "Sources", "App", "main.swift");
        var text = "func greet(_ name: String) -> String {\n    var greeting = \"Hello, \\(name)\"\n    return greeting\n}\nprint(greet(name: \"Swift\"))\n";
        await File.WriteAllTextAsync(path, text);
        var uri = new Uri(path).AbsoluteUri;
        var diagnostics = new JsonArray();
        var gate = new object();
        var report = new JsonObject();
        await using var rpc = new LspSession(Path.GetFullPath(server), workspace);
        var bridge = new VisualSwift.SwiftCodeActions { ResolveAsync = action => ResolveAsync(action) };
        async Task<Newtonsoft.Json.Linq.JToken> ResolveAsync(Newtonsoft.Json.Linq.JToken action) => Newtonsoft.Json.Linq.JToken.Parse((await rpc.Request("codeAction/resolve", JsonNode.Parse(action.ToString()))).ToJsonString());
        rpc.Notification = (method, parameters) =>
        {
            bridge.Notification(method, parameters == null ? null : Newtonsoft.Json.Linq.JToken.Parse(parameters.ToJsonString()));
            if (method == "textDocument/publishDiagnostics" && parameters?["uri"]?.ToString() == uri)
                lock (gate) diagnostics = parameters["diagnostics"]!.DeepClone().AsArray();
        };
        try
        {
            report["initialize"] = (await rpc.Request("initialize", new { processId = Environment.ProcessId, rootUri = new Uri(workspace + Path.DirectorySeparatorChar).AbsoluteUri, initializationOptions = new { backgroundIndexing = true }, capabilities = new { textDocument = new { codeAction = new { codeActionLiteralSupport = new { codeActionKind = new { valueSet = new[] { "quickfix", "refactor", "refactor.extract" } } }, dataSupport = true, resolveSupport = new { properties = new[] { "edit", "command" } } } } } })).DeepClone();
            await rpc.Notify("initialized", new { });
            await rpc.Notify("textDocument/didOpen", new { textDocument = new { uri, languageId = "swift", version = 1, text } });
            await rpc.Request("textDocument/hover", new { textDocument = new { uri }, position = new { line = 4, character = 8 } });
            for (var attempt = 0; attempt < 60; attempt++)
            {
                lock (gate) if (diagnostics.Count > 0) break;
                await Task.Delay(500);
            }
            JsonArray captured; lock (gate) captured = diagnostics.DeepClone().AsArray();
            report["diagnostics"] = captured.DeepClone();
            var stripped = captured.DeepClone().AsArray();
            foreach (var item in stripped) { item!.AsObject().Remove("codeActions"); item.AsObject().Remove("data"); }
            var request = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["range"] = new JsonObject { ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 }, ["end"] = new JsonObject { ["line"] = 5, ["character"] = 0 } }, ["context"] = new JsonObject { ["diagnostics"] = stripped, ["only"] = new JsonArray("quickfix"), ["triggerKind"] = 1 } };
            report["withoutBridge"] = (await rpc.Request("textDocument/codeAction", request)).DeepClone();
            var prepared = bridge.Prepare(Newtonsoft.Json.Linq.JToken.Parse(request.ToJsonString()));
            var actions = await rpc.Request("textDocument/codeAction", JsonNode.Parse(prepared.ToString()));
            actions = JsonNode.Parse((await bridge.CompleteAsync(Newtonsoft.Json.Linq.JToken.Parse(actions.ToJsonString()))).ToString());
            report["actions"] = actions?.DeepClone();
            report["bridgeChecksPassed"] = true;
            if (actions is not JsonArray array || array.Count == 0) throw new InvalidOperationException("SourceKit-LSP returned no code actions.");
            if (!array.Any(item => item?["title"]?.ToString().Contains("let", StringComparison.Ordinal) == true) || !array.Any(item => item?["title"]?.ToString().Contains("name:", StringComparison.Ordinal) == true)) throw new InvalidOperationException("Expected both compiler fixes.");
            var edits = array.SelectMany(action => action!["edit"]!["changes"]![uri]!.AsArray()).ToArray();
            int Offset(JsonNode position) { var start = 0; for (var line = 0; line < position["line"]!.GetValue<int>(); line++) start = text.IndexOf('\n', start) + 1; return start + position["character"]!.GetValue<int>(); }
            foreach (var edit in edits.OrderByDescending(edit => Offset(edit!["range"]!["start"]!))) { var start = Offset(edit!["range"]!["start"]!); var end = Offset(edit["range"]!["end"]!); text = text.Remove(start, end - start).Insert(start, edit["newText"]!.ToString()); }
            await File.WriteAllTextAsync(path, text);
            if (text.Contains("var greeting", StringComparison.Ordinal) || text.Contains("greet(name:", StringComparison.Ordinal)) throw new InvalidOperationException("Fix-it edits did not change the expected code.");
            report["fixedText"] = text;
            await rpc.Notify("textDocument/didChange", new { textDocument = new { uri, version = 2 }, contentChanges = new[] { new { text } } });
            var refactors = await rpc.Request("textDocument/codeAction", new { textDocument = new { uri }, range = new { start = new { line = 1, character = 19 }, end = new { line = 1, character = 35 } }, context = new { diagnostics = Array.Empty<object>(), only = new[] { "refactor" }, triggerKind = 1 } });
            report["refactors"] = refactors?.DeepClone();
            report["resolvedRefactors"] = JsonNode.Parse((await bridge.CompleteAsync(Newtonsoft.Json.Linq.JToken.Parse(refactors!.ToJsonString()))).ToString());
            var compiler = Path.Combine(Path.GetDirectoryName(server)!, "swift.exe");
            using (var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(compiler, "build --package-path \"" + workspace + "\"") { WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true })!)
            {
                var output = build.StandardOutput.ReadToEndAsync(); var error = build.StandardError.ReadToEndAsync();
                await build.WaitForExitAsync();
                report["buildOutput"] = await output + await error;
                if (build.ExitCode != 0) throw new InvalidOperationException("Code after Fix-it did not compile.");
            }
            report["passed"] = true;
            await rpc.Request("shutdown", null); await rpc.Notify("exit", null); rpc.CloseInput(); await rpc.WaitForExit();
        }
        catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); Environment.ExitCode = 1; }
        finally { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!); await File.WriteAllTextAsync(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine(report.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
    }
}
