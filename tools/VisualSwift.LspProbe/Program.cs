using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length == 4 && args[3] == "new-file")
{
    await NewFileChecks.Run(args[0], args[1], args[2]);
    return;
}

if (args.Length != 3)
    throw new ArgumentException("Usage: VisualSwift.LspProbe <sourcekit-lsp.exe> <package-directory> <report.json>");

var packageDirectory = Path.GetFullPath(args[1]);
var sourcePath = Path.Combine(packageDirectory, "Sources", "LspSmoke", "main.swift");
var source = await File.ReadAllTextAsync(sourcePath);
var uri = new Uri(sourcePath).AbsoluteUri;
var report = new JsonObject { ["packageUri"] = new Uri(packageDirectory + Path.DirectorySeparatorChar).AbsoluteUri };
await ProtocolChecks.Run();
report["framingChecksPassed"] = true;
await using var rpc = new LspSession(Path.GetFullPath(args[0]), packageDirectory);
try
{
    var initialize = await rpc.Request("initialize", new
    {
        processId = Environment.ProcessId,
        initializationOptions = new { backgroundIndexing = true },
        rootUri = new Uri(packageDirectory + Path.DirectorySeparatorChar).AbsoluteUri,
        workspaceFolders = new[] { new { uri = new Uri(packageDirectory + Path.DirectorySeparatorChar).AbsoluteUri, name = "LspSmoke" } },
        capabilities = new
        {
            general = new { positionEncodings = new[] { "utf-16" } },
            textDocument = new
            {
                completion = new { completionItem = new { snippetSupport = true } },
                hover = new { contentFormat = new[] { "markdown", "plaintext" } },
                diagnostic = new { dynamicRegistration = false },
                publishDiagnostics = new { versionSupport = true }
            },
            workspace = new { configuration = true, workspaceFolders = true }
        }
    });
    report["initialize"] = initialize.DeepClone();
    await rpc.Notify("initialized", new { });
    await rpc.Notify("textDocument/didOpen", new { textDocument = new { uri, languageId = "swift", version = 1, text = source } });

    async Task<JsonNode> At(string method, string marker, int offset = 0)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal) + offset;
        if (index < 0) throw new InvalidOperationException("Marker not found: " + marker);
        var prefix = source[..index];
        var position = new { line = prefix.Count(c => c == '\n'), character = index - (prefix.LastIndexOf('\n') + 1) };
        return await rpc.Request(method, new { textDocument = new { uri }, position });
    }

    var hover = await At("textDocument/hover", "greeter.greeting", 9);
    report["hover"] = hover.DeepClone();
    if (hover["contents"] == null) throw new InvalidOperationException("Hover returned no contents.");

    var definition = await At("textDocument/definition", "greeter.greeting", 9);
    report["definition"] = definition.DeepClone();
    if (definition is JsonArray { Count: 0 }) throw new InvalidOperationException("Definition returned no locations.");

    var completion = await At("textDocument/completion", "greeter.greeting", 8);
    report["completion"] = completion.DeepClone();
    var items = completion is JsonArray array ? array : completion["items"]?.AsArray();
    if (items == null || !items.Any(item => item?["label"]?.ToString().Contains("greeting", StringComparison.Ordinal) == true))
        throw new InvalidOperationException("Completion did not include greeting.");

    JsonNode symbols = new JsonArray();
    for (var attempt = 0; attempt < 30; attempt++)
    {
        symbols = await rpc.Request("workspace/symbol", new { query = "Greeter" });
        if (symbols is JsonArray matches && matches.Any(item => item?["name"]?.ToString() == "Greeter")) break;
        await Task.Delay(500);
    }
    report["workspaceSymbols"] = symbols.DeepClone();
    if (symbols is not JsonArray symbolItems || !symbolItems.Any(item => item?["name"]?.ToString() == "Greeter"))
        throw new InvalidOperationException("Workspace symbol search did not find Greeter.");
    var symbolLocation = symbolItems.First(item => item?["name"]?.ToString() == "Greeter")?["location"];
    if (symbolLocation?["uri"]?.ToString() != uri || symbolLocation?["range"]?["start"]?["line"]?.GetValue<int>() != 0 || symbolLocation?["range"]?["start"]?["character"]?.GetValue<int>() != 7)
        throw new InvalidOperationException("Symbol navigation did not resolve the declaration position.");
    var missingSymbols = await rpc.Request("workspace/symbol", new { query = "VisualSwiftDefinitelyMissingSymbol" });
    if (missingSymbols is not JsonArray { Count: 0 }) throw new InvalidOperationException("Unknown symbol search returned results.");
    report["symbolSearchVerified"] = true;
    JsonNode closedSymbols = new JsonArray();
    for (var attempt = 0; attempt < 30; attempt++)
    {
        closedSymbols = await rpc.Request("workspace/symbol", new { query = "ClosedFileGreeter" });
        if (closedSymbols is JsonArray { Count: > 0 }) break;
        await Task.Delay(500);
    }
    report["closedFileSymbols"] = closedSymbols.DeepClone();
    var closedItem = (closedSymbols as JsonArray)?.FirstOrDefault(item => item?["name"]?.ToString() == "ClosedFileGreeter");
    var closedLocation = closedItem?["location"];
    if (closedLocation?["uri"]?.ToString() != new Uri(Path.Combine(packageDirectory, "Sources", "LspSmoke", "SearchFixture.swift")).AbsoluteUri || closedLocation?["range"]?["start"]?["line"]?.GetValue<int>() != 0 || closedLocation?["range"]?["start"]?["character"]?.GetValue<int>() != 7)
        throw new InvalidOperationException("Workspace search did not resolve the closed-file declaration.");
    report["closedFileSearchVerified"] = true;

    var markerIndex = source.IndexOf("greeter.greeting", StringComparison.Ordinal) + 9;
    var markerPrefix = source[..markerIndex];
    var markerPosition = new { line = markerPrefix.Count(c => c == '\n'), character = markerIndex - (markerPrefix.LastIndexOf('\n') + 1) };
    JsonNode references = new JsonArray();
    for (var attempt = 0; attempt < 30; attempt++)
    {
        references = await rpc.Request("textDocument/references", new { textDocument = new { uri }, position = markerPosition, context = new { includeDeclaration = true } });
        if (references is JsonArray entries && entries.Any(item => item?["uri"]?.ToString().EndsWith("/SearchFixture.swift", StringComparison.Ordinal) == true)) break;
        await Task.Delay(500);
    }
    report["references"] = references.DeepClone();
    report["referencesVerified"] = references is JsonArray { Count: > 0 };
    report["crossFileReferencesVerified"] = references is JsonArray referenceItems && referenceItems.Any(item => item?["uri"]?.ToString().EndsWith("/SearchFixture.swift", StringComparison.Ordinal) == true);
    if (report["crossFileReferencesVerified"]?.GetValue<bool>() != true)
        throw new InvalidOperationException("Find references did not include the second source file.");
    var rename = await rpc.Request("textDocument/rename", new { textDocument = new { uri }, position = markerPosition, newName = "welcome" });
    report["rename"] = rename.DeepClone();
    report["renameVerified"] = rename["changes"] != null || rename["documentChanges"] != null;
    report["renameChecks"] = await RenameChecks.Run(rpc, packageDirectory, args[2]);
    report["renameFileChanges"] = await RenameFileChanges.Run(rpc, packageDirectory);
    report["formattingChecks"] = await FormattingChecks.Run(rpc, packageDirectory, args[2]);
    report["editorFeatures"] = await EditorFeatureChecks.Run(rpc, packageDirectory);

    await rpc.Notify("textDocument/didChange", new { textDocument = new { uri, version = 2 }, contentChanges = new[] { new { text = source + "\nlet invalidValue: Int = \"wrong\"\n" } } });
    var diagnostics = await rpc.Request("textDocument/diagnostic", new { textDocument = new { uri } });
    for (var attempt = 0; attempt < 20 && !(diagnostics["items"] is JsonArray entries && entries.Any(item => item?["severity"]?.GetValue<int>() == 1)); attempt++)
    {
        await Task.Delay(500);
        diagnostics = await rpc.Request("textDocument/diagnostic", new { textDocument = new { uri } });
    }
    report["diagnostics"] = diagnostics.DeepClone();
    if (diagnostics["items"] is not JsonArray diagnosticItems || !diagnosticItems.Any(item => item?["severity"]?.GetValue<int>() == 1))
        throw new InvalidOperationException("Expected an error diagnostic for assigning a String to Int.");

    await rpc.Notify("textDocument/didClose", new { textDocument = new { uri } });
    await rpc.Request("shutdown", null);
    await rpc.Notify("exit", null);
    rpc.CloseInput();
    await rpc.WaitForExit();
    report["serverExitCode"] = rpc.ExitCode;
    report["passed"] = rpc.ExitCode == 0;
    if (rpc.ExitCode != 0) throw new InvalidOperationException("SourceKit-LSP exited unsuccessfully.");
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
    var reportPath = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    await File.WriteAllTextAsync(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(report["passed"]?.GetValue<bool>() == true ? "LSP smoke checks passed." : "LSP smoke checks failed: " + report["error"]);
    Console.WriteLine("Report: " + reportPath);
}

sealed class LspSession : IAsyncDisposable
{
    public string Executable { get; }
    private readonly Process process;
    private readonly Stream input;
    private readonly Stream output;
    private readonly Task reader;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> pending = new();
    private readonly SemaphoreSlim writeGate = new(1);
    private readonly StringBuilder stderr = new();
    private int nextId;

    public LspSession(string executable, string workspace)
    {
        Executable = executable;
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.Environment["PATH"] = Path.GetDirectoryName(executable) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        process = new Process { StartInfo = info };
        process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data != null) lock (stderr) stderr.AppendLine(eventArgs.Data); };
        process.Start();
        process.BeginErrorReadLine();
        input = new VisualSwift.LspInputStream(process.StandardInput.BaseStream, workspace);
        output = process.StandardOutput.BaseStream;
        reader = ReadMessages();
    }

    public string Stderr { get { lock (stderr) return stderr.ToString(); } }
    public int ExitCode => process.ExitCode;

    public async Task<JsonNode> Request(string method, object? parameters)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            await Send(new { jsonrpc = "2.0", id, method, @params = parameters });
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
        }
        finally { pending.TryRemove(id, out _); }
    }

    public Task Notify(string method, object? parameters) => Send(new { jsonrpc = "2.0", method, @params = parameters });
    public void CloseInput() => input.Dispose();
    public async Task WaitForExit()
    {
        for (var attempt = 0; attempt < 300 && !process.HasExited; attempt++) await Task.Delay(100);
        if (!process.HasExited) throw new TimeoutException("SourceKit-LSP did not exit within 30 seconds.");
    }

    private async Task Send(object message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
        await writeGate.WaitAsync();
        try
        {
            await input.WriteAsync(header);
            await input.WriteAsync(body);
            await input.FlushAsync();
        }
        finally { writeGate.Release(); }
    }

    private async Task ReadMessages()
    {
        try
        {
            while (true)
            {
                var header = new StringBuilder();
                var oneByte = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await output.ReadAsync(oneByte) == 0) throw new EndOfStreamException();
                    header.Append((char)oneByte[0]);
                    if (header.Length > 8192) throw new InvalidDataException("LSP header is too large.");
                }
                var lengthLine = header.ToString().Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var length = int.Parse(lengthLine.Split(':')[1].Trim());
                if (length < 0 || length > 32 * 1024 * 1024) throw new InvalidDataException("Invalid LSP content length.");
                var body = new byte[length];
                await output.ReadExactlyAsync(body);
                var message = JsonNode.Parse(body)!;
                if (message["method"] != null && message["id"] != null)
                {
                    object? result = null;
                    if (message["method"]!.ToString() == "workspace/configuration")
                        result = message["params"]?["items"]?.AsArray().Select(_ => (object?)null).ToArray();
                    await Send(new { jsonrpc = "2.0", id = message["id"]!.DeepClone(), result });
                }
                else if (message["id"] is JsonValue idNode && idNode.TryGetValue<int>(out var id) && pending.TryGetValue(id, out var completion))
                {
                    if (message["error"] != null) completion.TrySetException(new InvalidOperationException(message["error"]!.ToJsonString()));
                    else completion.TrySetResult(message["result"]?.DeepClone() ?? JsonValue.Create((string?)null)!);
                }
            }
        }
        catch (Exception error)
        {
            foreach (var completion in pending.Values) completion.TrySetException(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await WaitForExit();
        output.Dispose();
        await reader.WaitAsync(TimeSpan.FromSeconds(5));
        process.Dispose();
        writeGate.Dispose();
    }
}
