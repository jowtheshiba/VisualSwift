using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

static class HierarchyChecks
{
    public static async Task Run(string server, string workspace, string reportPath)
    {
        workspace = Path.GetFullPath(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "Sources", "App"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "Package.swift"), "// swift-tools-version: 6.0\nimport PackageDescription\nlet package = Package(name: \"HierarchySmoke\", targets: [.executableTarget(name: \"App\")])\n");
        var modelPath = Path.Combine(workspace, "Sources", "App", "Models.swift");
        var model = "protocol Greeter { func greet() }\nclass Base: Greeter {\n    func greet() { helper() }\n}\nclass Child: Base {}\nfunc helper() { leaf() }\nfunc leaf() {}\n";
        var mainPath = Path.Combine(workspace, "Sources", "App", "main.swift");
        var main = "func caller() { helper() }\ncaller()\nChild().greet()\n";
        await File.WriteAllTextAsync(modelPath, model);
        await File.WriteAllTextAsync(mainPath, main);
        var report = new JsonObject();
        try
        {
            var compiler = Path.Combine(Path.GetDirectoryName(server)!, "swift.exe");
            using (var build = Process.Start(new ProcessStartInfo(compiler, "build --package-path \"" + workspace + "\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!)
            {
                var output = build.StandardOutput.ReadToEndAsync(); var error = build.StandardError.ReadToEndAsync();
                await build.WaitForExitAsync(); report["buildOutput"] = await output + await error;
                if (build.ExitCode != 0) throw new InvalidOperationException("Hierarchy fixture build failed.");
            }
            await using var rpc = new LspSession(server, workspace);
            report["initialize"] = (await rpc.Request("initialize", new { processId = Environment.ProcessId, rootUri = new Uri(workspace + Path.DirectorySeparatorChar).AbsoluteUri, capabilities = new { textDocument = new { callHierarchy = new { dynamicRegistration = false }, typeHierarchy = new { dynamicRegistration = false } } } })).DeepClone();
            await rpc.Notify("initialized", new { });
            var uri = new Uri(modelPath).AbsoluteUri;
            await rpc.Notify("textDocument/didOpen", new { textDocument = new { uri, languageId = "swift", version = 1, text = model } });
            await rpc.Notify("textDocument/didOpen", new { textDocument = new { uri = new Uri(mainPath).AbsoluteUri, languageId = "swift", version = 1, text = main } });
            async Task<JsonNode> Prepare(string method, int line, int character)
            {
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    await rpc.Request("textDocument/hover", new { textDocument = new { uri }, position = new { line, character } });
                    var items = await rpc.Request(method, new { textDocument = new { uri }, position = new { line, character } });
                    if (items is JsonArray array && array.Count > 0) return array[0]!;
                    await Task.Delay(500);
                }
                throw new InvalidOperationException("No hierarchy item for " + method);
            }
            async Task<JsonNode> Related(string method, JsonNode item, string expected)
            {
                JsonNode? result = null;
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    result = await rpc.Request(method, new { item });
                    if (result?.ToJsonString().Contains(expected, StringComparison.Ordinal) == true) return result;
                    await Task.Delay(500);
                }
                throw new InvalidOperationException(method + " is missing " + expected + ": " + result);
            }
            var helper = await Prepare("textDocument/prepareCallHierarchy", 5, 6);
            report["callItem"] = helper.DeepClone();
            report["incoming"] = (await Related("callHierarchy/incomingCalls", helper, "caller")).DeepClone();
            report["outgoing"] = (await Related("callHierarchy/outgoingCalls", helper, "leaf")).DeepClone();
            var child = await Prepare("textDocument/prepareTypeHierarchy", 4, 7);
            report["baseTypes"] = (await Related("typeHierarchy/supertypes", child, "Base")).DeepClone();
            var baseType = await Prepare("textDocument/prepareTypeHierarchy", 1, 7);
            report["derivedTypes"] = (await Related("typeHierarchy/subtypes", baseType, "Child")).DeepClone();
            report["protocols"] = (await Related("typeHierarchy/supertypes", baseType, "Greeter")).DeepClone();
            var incoming = report["incoming"]!.AsArray();
            if (!incoming.Any(call => call?["from"]?["uri"]?.ToString() == new Uri(mainPath).AbsoluteUri && call?["fromRanges"]?.AsArray().Count > 0)) throw new InvalidOperationException("Cross-file call sites are missing.");
            report["passed"] = true;
            await rpc.Request("shutdown", null); await rpc.Notify("exit", null); rpc.CloseInput(); await rpc.WaitForExit();
        }
        catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); Environment.ExitCode = 1; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
