using System.Collections.Concurrent;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using VisualSwift;

static class MultiProjectChecks
{
    public static async Task Run(string server, string directory, string reportPath)
    {
        directory = Path.GetFullPath(directory);
        var roots = new[] { Path.Combine(directory, "Alpha"), Path.Combine(directory, "Beta") };
        var report = new JObject();
        var processes = new List<Process>();
        try
        {
            await CheckRouting(directory);
            report["routingChecksPassed"] = true;
            for (var index = 0; index < roots.Length; index++)
            {
                var root = roots[index]; var name = Path.GetFileName(root);
                Directory.CreateDirectory(Path.Combine(root, "Sources", "App"));
                await File.WriteAllTextAsync(Path.Combine(root, "Package.swift"), "// swift-tools-version: 6.0\nimport PackageDescription\nlet package = Package(name: \"" + name + "\", targets: [.executableTarget(name: \"App\")])\n");
                await File.WriteAllTextAsync(Path.Combine(root, "Sources", "App", "Person.swift"), "struct Person { let " + name.ToLowerInvariant() + "Only: Int }\n");
                await File.WriteAllTextAsync(Path.Combine(root, "Sources", "App", "main.swift"), "let person = Person(" + name.ToLowerInvariant() + "Only: 1)\nprint(person." + name.ToLowerInvariant() + "Only)\n");
                var start = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(server)!, "swift.exe"), "build --package-path \"" + root + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var build = Process.Start(start)!;
                var output = build.StandardOutput.ReadToEndAsync(); var error = build.StandardError.ReadToEndAsync();
                await build.WaitForExitAsync(); report[name + "Build"] = await output + await error;
                if (build.ExitCode != 0) throw new InvalidOperationException(name + " fixture did not build.");
            }
            using var broker = new SwiftLspBroker(roots, (root, token) =>
            {
                var info = new ProcessStartInfo(server) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                info.Environment["PATH"] = Path.GetDirectoryName(server) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
                var process = Process.Start(info)!; processes.Add(process); process.BeginErrorReadLine();
                return Task.FromResult(new SwiftLspEndpoint(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, () => { if (!process.HasExited) process.Kill(entireProcessTree: true); }));
            });
            await using var client = new BrokerClient(broker);
            var initialized = await client.Request("initialize", new JObject { ["processId"] = Environment.ProcessId, ["capabilities"] = new JObject { ["workspace"] = new JObject { ["configuration"] = true }, ["textDocument"] = new JObject { ["completion"] = new JObject { ["completionItem"] = new JObject { ["resolveSupport"] = new JObject { ["properties"] = new JArray("documentation", "detail") } } }, ["semanticTokens"] = new JObject { ["requests"] = new JObject { ["full"] = true, ["range"] = true }, ["tokenTypes"] = new JArray("class", "struct", "variable", "property", "function", "method", "typeParameter"), ["tokenModifiers"] = new JArray("static", "defaultLibrary"), ["formats"] = new JArray("relative") } } } });
            report["initializedServers"] = processes.Count;
            report["legend"] = initialized["capabilities"]?["semanticTokensProvider"]?["legend"]?.DeepClone();
            await client.Notify("initialized", new JObject());
            var uris = roots.Select(root => new Uri(Path.Combine(root, "Sources", "App", "main.swift")).AbsoluteUri).ToArray();
            for (var index = 0; index < roots.Length; index++)
                await client.Notify("textDocument/didOpen", new JObject { ["textDocument"] = new JObject { ["uri"] = uris[index], ["languageId"] = "swift", ["version"] = 1, ["text"] = await File.ReadAllTextAsync(Path.Combine(roots[index], "Sources", "App", "main.swift")) } });
            for (var index = 0; index < roots.Length; index++)
            {
                var name = Path.GetFileName(roots[index]);
                var expected = name.ToLowerInvariant() + "Only"; var unexpected = index == 0 ? "betaOnly" : "alphaOnly";
                var input = new JObject { ["textDocument"] = new JObject { ["uri"] = uris[index] }, ["position"] = new JObject { ["line"] = 1, ["character"] = 13 } };
                JArray items = new();
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    var completion = await client.Request("textDocument/completion", input);
                    items = completion as JArray ?? completion["items"] as JArray ?? new JArray();
                    if (items.Any(item => (string?)item["label"] == expected)) break;
                    await Task.Delay(400);
                }
                var labels = items.Select(item => (string?)item["label"]).ToArray();
                report[name + "Completion"] = new JArray(labels.Select(label => (JToken)new JValue(label)));
                if (!labels.Contains(expected) || labels.Contains(unexpected)) throw new InvalidOperationException(name + " completion leaked another project's symbols.");
                var selected = items.First(item => (string?)item["label"] == expected);
                if ((string?)selected["data"]?["__visualswiftSession"] != roots[index]) throw new InvalidOperationException("Completion resolve has no project context.");
                var resolved = await client.Request("completionItem/resolve", selected);
                report[name + "Resolved"] = resolved;
                var definition = await client.Request("textDocument/definition", new JObject { ["textDocument"] = new JObject { ["uri"] = uris[index] }, ["position"] = new JObject { ["line"] = 0, ["character"] = 16 } });
                report[name + "Definition"] = definition;
                if (!definition.ToString().Contains(new Uri(Path.Combine(roots[index], "Sources", "App", "Person.swift")).AbsoluteUri, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Go To Definition selected the wrong Swift project.");
                var tokens = await client.Request("textDocument/semanticTokens/full", new JObject { ["textDocument"] = new JObject { ["uri"] = uris[index] } });
                report[name + "Tokens"] = tokens;
                if ((tokens["data"] as JArray)?.Count == 0) throw new InvalidOperationException(name + " semantic tokens are empty.");
            }
            var changed = "let person = Person(alphaOnly: 1)\nprint(person.missingProperty)\n";
            await client.Notify("textDocument/didChange", new JObject { ["textDocument"] = new JObject { ["uri"] = uris[0], ["version"] = 2 }, ["contentChanges"] = new JArray(new JObject { ["text"] = changed }) });
            await client.Request("textDocument/hover", new JObject { ["textDocument"] = new JObject { ["uri"] = uris[0] }, ["position"] = new JObject { ["line"] = 1, ["character"] = 18 } });
            for (var attempt = 0; attempt < 30 && !client.Diagnostics.TryGetValue(uris[0], out var diagnosticsWithError); attempt++) await Task.Delay(200);
            var hasAlphaError = client.Diagnostics.TryGetValue(uris[0], out var alphaErrors) && alphaErrors.Any(item => ((string?)item["message"])?.Contains("missingProperty", StringComparison.Ordinal) == true);
            for (var attempt = 0; attempt < 30 && !hasAlphaError; attempt++)
            {
                await Task.Delay(200);
                hasAlphaError = client.Diagnostics.TryGetValue(uris[0], out alphaErrors) && alphaErrors.Any(item => ((string?)item["message"])?.Contains("missingProperty", StringComparison.Ordinal) == true);
            }
            report["AlphaDiagnostics"] = alphaErrors;
            report["BetaDiagnostics"] = client.Diagnostics.TryGetValue(uris[1], out var betaErrors) ? betaErrors : new JArray();
            if (!hasAlphaError || betaErrors?.Any(item => (int?)item["severity"] == 1) == true) throw new InvalidOperationException("Diagnostics are not isolated by project.");
            var symbols = await client.Request("workspace/symbol", new JObject { ["query"] = "Person" });
            report["WorkspaceSymbols"] = symbols;
            await client.Request("shutdown", null); await client.Notify("exit", null);
            for (var attempt = 0; attempt < 30 && processes.Any(process => !process.HasExited); attempt++) await Task.Delay(100);
            if (processes.Any(process => !process.HasExited)) throw new InvalidOperationException("Project language servers survived shutdown.");
            report["passed"] = true;
        }
        catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); Environment.ExitCode = 1; }
        finally { foreach (var process in processes) { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); } }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, report.ToString()); Console.WriteLine(report.ToString());
    }

    private static async Task CheckRouting(string directory)
    {
        var roots = new[] { Path.Combine(directory, "FakeAlpha"), Path.Combine(directory, "FakeBeta") };
        var outputs = new Dictionary<string, SwiftMessageOutput>();
        var restoredData = false; var executedBeta = false; var callbacks = 0;
        using var broker = new SwiftLspBroker(roots, (root, token) =>
        {
            var beta = root == roots[1]; var output = new SwiftMessageOutput(); outputs[root] = output;
            var input = new SwiftMessageInput(message =>
            {
                var method = (string?)message["method"]; var id = message["id"];
                if (method == null && id != null) { callbacks++; return Task.CompletedTask; }
                JToken result = JValue.CreateNull();
                if (method == "initialize")
                {
                    output.Enqueue(new JObject { ["jsonrpc"] = "2.0", ["method"] = "workspace/configuration", ["id"] = 77, ["params"] = new JObject { ["items"] = new JArray(new JObject()) } });
                    result = new JObject { ["capabilities"] = new JObject { ["semanticTokensProvider"] = new JObject { ["full"] = true, ["legend"] = new JObject { ["tokenTypes"] = beta ? new JArray("property", "class") : new JArray("class", "property"), ["tokenModifiers"] = beta ? new JArray("defaultLibrary", "static") : new JArray("static", "defaultLibrary") } }, ["executeCommandProvider"] = new JObject { ["commands"] = new JArray("sameCommand") } } };
                }
                if (method == "textDocument/semanticTokens/full") result = new JObject { ["data"] = new JArray(0, 0, 1, 0, 1) };
                if (method == "textDocument/completion") result = new JArray(new JObject { ["label"] = "item", ["data"] = 7 });
                if (method == "completionItem/resolve") { restoredData = beta && (int?)message["params"]?["data"] == 7; result = message["params"]!.DeepClone(); }
                if (method == "textDocument/codeAction") result = new JArray(new JObject { ["title"] = "Run", ["command"] = new JObject { ["title"] = "Run", ["command"] = "sameCommand" } });
                if (method == "workspace/executeCommand") { executedBeta = beta && (string?)message["params"]?["command"] == "sameCommand"; }
                if (id != null) output.Enqueue(new JObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
                return Task.CompletedTask;
            });
            return Task.FromResult(new SwiftLspEndpoint(input, output, output.Complete));
        });
        await using var client = new BrokerClient(broker);
        await client.Request("initialize", new JObject { ["capabilities"] = new JObject() });
        var parameters = new JObject { ["textDocument"] = new JObject { ["uri"] = new Uri(Path.Combine(roots[1], "main.swift")).AbsoluteUri } };
        var tokens = await client.Request("textDocument/semanticTokens/full", parameters);
        if ((int?)tokens["data"]?[3] != 1 || (int?)tokens["data"]?[4] != 2) throw new InvalidOperationException("Different server token legends were not remapped.");
        var completion = await client.Request("textDocument/completion", parameters);
        await client.Request("completionItem/resolve", completion[0]);
        if (!restoredData) throw new InvalidOperationException("Project routing corrupted server completion data.");
        var actions = await client.Request("textDocument/codeAction", parameters);
        await client.Request("workspace/executeCommand", actions[0]!["command"]);
        if (!executedBeta) throw new InvalidOperationException("Identical server command names selected the wrong project.");
        if (callbacks != 2) throw new InvalidOperationException("Overlapping server request IDs were not routed to both projects.");
        outputs[roots[1]].Complete();
        await client.Request("textDocument/hover", new JObject { ["textDocument"] = new JObject { ["uri"] = new Uri(Path.Combine(roots[0], "main.swift")).AbsoluteUri } });
    }

    private sealed class BrokerClient : IAsyncDisposable
    {
        private readonly SwiftLspBroker broker;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JToken>> pending = new();
        private readonly Task reader;
        private int nextId;
        public ConcurrentDictionary<string, JArray> Diagnostics { get; } = new(StringComparer.OrdinalIgnoreCase);
        public BrokerClient(SwiftLspBroker broker) { this.broker = broker; reader = Read(); }
        public async Task<JToken> Request(string method, JToken? parameters)
        {
            var id = Interlocked.Increment(ref nextId); var completion = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously); pending[id] = completion;
            await Send(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters?.DeepClone() });
            try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(60)); } finally { pending.TryRemove(id, out _); }
        }
        public Task Notify(string method, JToken? parameters) => Send(new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });
        private async Task Send(JObject message) { var frame = SwiftMessageInput.Frame(message); await broker.Input.WriteAsync(frame); }
        private async Task Read()
        {
            var parser = new SwiftMessageInput(async message =>
            {
                if (message["method"] != null && message["id"] != null)
                {
                    JToken result = JValue.CreateNull();
                    if ((string?)message["method"] == "workspace/configuration") result = new JArray((message["params"]?["items"] as JArray ?? new JArray()).Select(_ => JValue.CreateNull()));
                    await Send(new JObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = result });
                }
                else if (message["id"]?.Type == JTokenType.Integer && pending.TryGetValue((int)message["id"]!, out var completion))
                {
                    if (message["error"] != null) completion.TrySetException(new InvalidOperationException(message["error"]!.ToString())); else completion.TrySetResult(message["result"] ?? JValue.CreateNull());
                }
                else if ((string?)message["method"] == "textDocument/publishDiagnostics") Diagnostics[(string)message["params"]!["uri"]!] = (JArray)message["params"]!["diagnostics"]!.DeepClone();
            });
            var bytes = new byte[4096]; int count;
            while ((count = await broker.Output.ReadAsync(bytes)) > 0) await parser.WriteAsync(bytes, 0, count);
        }
        public async ValueTask DisposeAsync() { broker.Dispose(); await reader.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
