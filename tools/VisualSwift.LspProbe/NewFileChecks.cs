using System.Text.Json;
using System.Text.Json.Nodes;

static class NewFileChecks
{
    public static async Task Run(string server, string workspace, string reportPath)
    {
        workspace = Path.GetFullPath(workspace);
        var sources = Path.Combine(workspace, "Sources", "App");
        Directory.CreateDirectory(sources);
        await File.WriteAllTextAsync(Path.Combine(workspace, "Package.swift"), "// swift-tools-version: 6.0\nimport PackageDescription\nlet package = Package(name: \"NewFileSmoke\", targets: [.executableTarget(name: \"App\")])\n");
        var main = Path.Combine(sources, "main.swift");
        var person = Path.Combine(sources, "Person.swift");
        var text = "let person = Person(name: \"Alex\", age: 23)\n";
        await File.WriteAllTextAsync(main, text);
        var uri = new Uri(main).AbsoluteUri;
        var report = new JsonObject();
        await using var rpc = new LspSession(Path.GetFullPath(server), workspace);
        try
        {
            await rpc.Request("initialize", new { processId = Environment.ProcessId, rootUri = new Uri(workspace + Path.DirectorySeparatorChar).AbsoluteUri, initializationOptions = new { backgroundIndexing = true }, capabilities = new { textDocument = new { completion = new { completionItem = new { snippetSupport = true } } } } });
            await rpc.Notify("initialized", new { });
            using var watcher = new VisualSwift.SwiftFileWatcher(workspace, changes => rpc.Notify("workspace/didChangeWatchedFiles", JsonNode.Parse(changes.ToString())));
            var refreshes = 0;
            watcher.StructureChanged = () => Interlocked.Increment(ref refreshes);
            await rpc.Notify("textDocument/didOpen", new { textDocument = new { uri, languageId = "swift", version = 1, text } });
            await rpc.Request("textDocument/hover", new { textDocument = new { uri }, position = new { line = 0, character = 15 } });
            await File.WriteAllTextAsync(person, "");
            await Task.Delay(500);
            await watcher.FlushAsync();
            await File.WriteAllTextAsync(person, "final class Person {\n let name: String\n let age: Int\n init(name: String, age: Int) { self.name = name; self.age = age }\n}\n");
            await rpc.Notify("textDocument/didOpen", new { textDocument = new { uri = new Uri(person).AbsoluteUri, languageId = "swift", version = 1, text = await File.ReadAllTextAsync(person) } });
            JsonNode? completion = null;
            var found = false;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                await watcher.FlushAsync();
                completion = await rpc.Request("textDocument/completion", new { textDocument = new { uri }, position = new { line = 0, character = 13 } });
                var items = completion is JsonArray array ? array : completion?["items"]?.AsArray();
                found = items?.Any(item => item?["label"]?.ToString().Contains("Person", StringComparison.Ordinal) == true) == true;
                if (found) break;
                await Task.Delay(250);
            }
            report["typeCompletion"] = found;
            report["structuralRefreshes"] = refreshes;
            if (refreshes == 0) throw new InvalidOperationException("Source file creation did not request a server refresh.");
            report["definition"] = (await rpc.Request("textDocument/definition", new { textDocument = new { uri }, position = new { line = 0, character = 15 } })).DeepClone();
            report["completion"] = completion?.DeepClone();
            if (!found) throw new InvalidOperationException("New Person.swift is absent from completion in the already-open main.swift.");
            await rpc.Notify("textDocument/didChange", new { textDocument = new { uri, version = 2 }, contentChanges = new[] { new { text = "let person = Person(\n" } } });
            var signature = await rpc.Request("textDocument/signatureHelp", new { textDocument = new { uri }, position = new { line = 0, character = 20 } });
            report["initializerSignature"] = signature.DeepClone();
            if (signature?["signatures"] is not JsonArray signatures || !signatures.Any(item => item?["label"]?.ToString().Contains("name", StringComparison.Ordinal) == true && item?["label"]?.ToString().Contains("age", StringComparison.Ordinal) == true)) throw new InvalidOperationException("Initializer signature is missing name and age.");
            var beforeChange = refreshes;
            await File.AppendAllTextAsync(person, "\n");
            await Task.Delay(500);
            await watcher.FlushAsync();
            if (refreshes != beforeChange) throw new InvalidOperationException("Saving existing source unexpectedly requested a restart.");
            report["passed"] = true;
            await rpc.Request("shutdown", null);
            await rpc.Notify("exit", null);
            rpc.CloseInput();
            await rpc.WaitForExit();
        }
        catch (Exception error)
        {
            report["passed"] = false;
            report["error"] = error.ToString();
            Environment.ExitCode = 1;
        }
        finally
        {
            report["stderr"] = rpc.Stderr;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("New-file completion passed: " + report["passed"]);
        }
    }
}
