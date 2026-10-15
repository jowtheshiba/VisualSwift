using System.Text.Json;
using System.Text.Json.Nodes;

static class CxxChecks
{
    public static async Task Run(string server, string workspace, string reportPath)
    {
        workspace = Path.GetFullPath(workspace); var report = new JsonObject();
        QuickInfoChecks.Run();
        try
        {
            await using var rpc = new LspSession(server, workspace);
            await rpc.Request("initialize", new { processId = Environment.ProcessId, rootUri = new Uri(workspace + Path.DirectorySeparatorChar).AbsoluteUri, capabilities = new { general = new { positionEncodings = new[] { "utf-16" } }, textDocument = new { completion = new { completionItem = new { snippetSupport = true } } } } });
            await rpc.Notify("initialized", new { });
            var source = Path.Combine(workspace, "Sources", "App", "main.swift"); var text = await File.ReadAllTextAsync(source); var uri = new Uri(source).AbsoluteUri;
            await rpc.Notify("textDocument/didOpen", new { textDocument = new { uri, languageId = "swift", version = 1, text } });
            var lines = text.Split('\n'); var line = Array.FindIndex(lines, value => value.Contains("calculator.add", StringComparison.Ordinal));
            var character = lines[line].IndexOf("add", StringComparison.Ordinal);
            JsonNode? completion = null;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await rpc.Request("textDocument/hover", new { textDocument = new { uri }, position = new { line, character = character + 1 } });
                completion = await rpc.Request("textDocument/completion", new { textDocument = new { uri }, position = new { line, character } });
                var items = completion as JsonArray ?? completion?["items"] as JsonArray;
                if (items?.Any(item => item?["label"]?.ToString().StartsWith("add", StringComparison.Ordinal) == true) == true) break;
                await Task.Delay(500);
            }
            report["completion"] = completion?.DeepClone();
            var returned = completion as JsonArray ?? completion?["items"] as JsonArray;
            if (returned?.Any(item => item?["label"]?.ToString().StartsWith("add", StringComparison.Ordinal) == true) != true) throw new InvalidOperationException("Swift completion did not include the imported C++ method.");
            var definition = await rpc.Request("textDocument/definition", new { textDocument = new { uri }, position = new { line, character = character + 1 } });
            report["definition"] = definition.DeepClone();
            if (!definition.ToJsonString().Contains("CppMath.h", StringComparison.Ordinal) && !definition.ToJsonString().Contains("CppMath.cpp", StringComparison.Ordinal)) throw new InvalidOperationException("Go To Definition did not reach C++ source or its public header.");
            report["hover"] = await rpc.Request("textDocument/hover", new { textDocument = new { uri }, position = new { line, character = character + 1 } });
            var typeLine = Array.FindIndex(lines, value => value.Contains("CppMath.Calculator", StringComparison.Ordinal));
            var typeHover = await rpc.Request("textDocument/hover", new { textDocument = new { uri }, position = new { line = typeLine, character = lines[typeLine].IndexOf("Calculator", StringComparison.Ordinal) + 1 } });
            report["typeHover"] = typeHover.DeepClone();
            var native = VisualSwift.SwiftQuickInfoContent.Parse(Newtonsoft.Json.Linq.JToken.Parse(typeHover["contents"]!.ToJsonString()));
            if (native.Signatures.Count != 2 || native.Documentation.Count != 0) throw new InvalidOperationException("Native C++ constructor Quick Info did not retain exactly its type and initializer signatures.");
            await rpc.Notify("textDocument/didClose", new { textDocument = new { uri } });
            report["passed"] = true;
        }
        catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); Environment.ExitCode = 1; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine(report.ToJsonString());
    }
}
