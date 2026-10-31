using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using VisualSwift;

static class BrokerRecoveryChecks
{
    public static async Task Run()
    {
        await ReplayAndIsolation();
        await RetryLimitAndDisposal();
        await RepeatedCrashes();
        Console.WriteLine("Broker recovery checks passed: unsaved edits, initialization callbacks, project isolation, file changes, retry limit and disposal.");
    }

    private static async Task ReplayAndIsolation()
    {
        var fixture = new Fixture("replay");
        using var broker = fixture.CreateBroker();
        await using var client = new MultiProjectChecks.BrokerClient(broker);
        await Initialize(client);
        var alpha = fixture.Roots[0]; var beta = fixture.Roots[1];
        var uri = new Uri(Path.Combine(alpha, "main.swift")).AbsoluteUri;
        var closed = new Uri(Path.Combine(alpha, "closed.swift")).AbsoluteUri;
        var opened = new Uri(Path.Combine(alpha, "new.swift")).AbsoluteUri;
        await client.Notify("workspace/didChangeConfiguration", new JObject { ["settings"] = new JObject { ["swift"] = new JObject { ["test"] = true } } });
        await Open(client, uri, "// 😀\r\nlet value = 1\r\n");
        await Open(client, closed, "closed");
        await Change(client, uri, 2, new JObject { ["range"] = Range(1, 12, 1, 13), ["text"] = "42" });

        var old = fixture.Servers[alpha];
        var pending = client.Request("textDocument/definition", At(uri));
        await old.DefinitionReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.HoldAlphaInitialization = true;
        old.Output.Complete();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("A crashed server left a request alive."); }
        catch (InvalidOperationException) { }
        await Until(() => fixture.Starts[alpha] == 2 && fixture.Servers[alpha].Callbacks == 1);
        var replacement = fixture.Servers[alpha];

        // These arrive while initialize is outstanding. The broker must remember edits without blocking Beta.
        await Change(client, uri, 3, new JObject { ["range"] = Range(0, 3, 0, 5), ["text"] = "🐕" });
        await Change(client, uri, 4, new JObject { ["range"] = Range(1, 12, 1, 14), ["text"] = "99" });
        await client.Notify("textDocument/didClose", At(closed));
        await Open(client, opened, "unsaved new file");
        await client.Notify("workspace/didChangeWatchedFiles", new JObject { ["changes"] = new JArray(new JObject { ["uri"] = opened, ["type"] = 1 }) });
        await client.Request("textDocument/hover", At(new Uri(Path.Combine(beta, "main.swift")).AbsoluteUri)).WaitAsync(TimeSpan.FromSeconds(2));
        Check(fixture.Starts[beta] == 1, "Beta restarted when Alpha crashed.");
        replacement.ReplyInitialize();
        await Until(() => fixture.States[alpha].State == "Running");
        var documents = await client.Request("textDocument/hover", At(uri));
        Check((string?)documents[uri]?["text"] == "// 🐕\r\nlet value = 99\r\n" && (int?)documents[uri]?["version"] == 4, "Recovery lost unsaved incremental edits or UTF-16 positions.");
        Check(documents[closed] == null && (string?)documents[opened]?["text"] == "unsaved new file", "Open/close during recovery was lost.");
        var methods = replacement.Notifications.Select(item => (string?)item["method"]).ToArray();
        Check(methods.Take(3).SequenceEqual(new[] { "initialized", "workspace/didChangeConfiguration", "workspace/didChangeWatchedFiles" }) && methods.Skip(3).All(method => method == "textDocument/didOpen"), "Recovery did not replay initialization, configuration, file changes and documents in order.");
        Check((string?)replacement.Notifications.ElementAt(2)["params"]?["changes"]?[0]?["uri"] == opened, "A file change during recovery was lost.");
        Check((bool?)replacement.Notifications.ElementAt(1)["params"]?["settings"]?["swift"]?["test"] == true, "Configuration was not preserved.");

        fixture.HoldAlphaInitialization = false;
        var refreshes = new ConcurrentQueue<Task>();
        var fileEvents = new ConcurrentQueue<JObject>();
        using var watcher = new SwiftFileWatcher(alpha, changes => { fileEvents.Enqueue((JObject)changes.DeepClone()); return Task.CompletedTask; });
        watcher.StructureChanged = () => refreshes.Enqueue(broker.RestartProjectAsync(alpha));
        await watcher.FlushAsync();
        var path = Path.Combine(alpha, Guid.NewGuid().ToString("N") + ".swift");
        await File.WriteAllTextAsync(path, "let added = 1");
        await Until(() => refreshes.Count == 1);
        await refreshes.Last().WaitAsync(TimeSpan.FromSeconds(5));
        while (fileEvents.TryDequeue(out _)) { }
        await File.WriteAllTextAsync(path, "let added = 2");
        await Until(() => fileEvents.SelectMany(item => (JArray)item["changes"]!).Any(change => (string?)change["uri"] == new Uri(path).AbsoluteUri && (int?)change["type"] == 2));
        Check(refreshes.Count == 1, "Saving existing source restarted its language server.");
        var renamed = Path.ChangeExtension(path, ".renamed.swift");
        File.Move(path, renamed);
        await Until(() => refreshes.Count == 2);
        await refreshes.Last().WaitAsync(TimeSpan.FromSeconds(5));
        File.Delete(renamed);
        await Until(() => refreshes.Count == 3);
        await refreshes.Last().WaitAsync(TimeSpan.FromSeconds(5));
        Check(fixture.Starts[alpha] == 5 && fixture.Starts[beta] == 1, "Create/rename/delete restarted an unrelated project.");
        var afterRefresh = await client.Request("textDocument/hover", At(uri));
        Check((string?)afterRefresh[uri]?["text"] == "// 🐕\r\nlet value = 99\r\n", "A file structure refresh lost unsaved text.");
        Console.WriteLine("Passed recovery replay and isolated create/rename/delete.");
    }

    private static async Task RetryLimitAndDisposal()
    {
        var fixture = new Fixture("limits");
        using var broker = fixture.CreateBroker();
        await using var client = new MultiProjectChecks.BrokerClient(broker);
        await Initialize(client);
        var alpha = fixture.Roots[0]; var beta = fixture.Roots[1];
        fixture.FailAlphaStart = true;
        fixture.Servers[alpha].Output.Complete();
        await Until(() => fixture.States[alpha].Error?.Contains("recovery stopped", StringComparison.Ordinal) == true, 15000);
        Check(fixture.Starts[alpha] == 4, "Automatic recovery did not stop after three failed attempts.");
        await client.Request("textDocument/hover", At(new Uri(Path.Combine(beta, "main.swift")).AbsoluteUri));
        fixture.FailAlphaStart = false;
        await broker.RestartProjectAsync(alpha).WaitAsync(TimeSpan.FromSeconds(5));
        Check(fixture.States[alpha].State == "Running" && fixture.Starts[alpha] == 5, "An explicit restart did not reset the recovery limit.");

        fixture.Servers[alpha].Output.Complete();
        await Until(() => fixture.States[alpha].State == "Restarting");
        var starts = fixture.Starts[alpha];
        broker.Dispose();
        await Task.Delay(1200);
        Check(fixture.Starts[alpha] == starts && fixture.Servers.Values.All(server => server.Disposed), "A server restarted or survived after broker disposal.");
        Console.WriteLine("Passed bounded retries, explicit retry and cancellation during backoff.");
    }

    private static async Task RepeatedCrashes()
    {
        var fixture = new Fixture("crash-loop");
        using var broker = fixture.CreateBroker();
        await using var client = new MultiProjectChecks.BrokerClient(broker);
        await Initialize(client);
        var alpha = fixture.Roots[0];
        for (var count = 2; count <= 4; count++)
        {
            fixture.Servers[alpha].Output.Complete();
            await Until(() => fixture.Starts[alpha] == count && fixture.States[alpha].State == "Running");
        }
        fixture.Servers[alpha].Output.Complete();
        await Until(() => fixture.States[alpha].Error?.Contains("recovery stopped", StringComparison.Ordinal) == true);
        Check(fixture.Starts[alpha] == 4, "Successful initialization reset the crash-loop budget.");
        Console.WriteLine("Passed retry budget across repeated successful restarts followed by crashes.");
    }

    private static async Task Initialize(MultiProjectChecks.BrokerClient client)
    {
        await client.Request("initialize", new JObject { ["capabilities"] = new JObject() });
        await client.Notify("initialized", new JObject());
    }
    private static JObject At(string uri) => new() { ["textDocument"] = new JObject { ["uri"] = uri } };
    private static JObject Range(int line, int character, int endLine, int endCharacter) => new() { ["start"] = new JObject { ["line"] = line, ["character"] = character }, ["end"] = new JObject { ["line"] = endLine, ["character"] = endCharacter } };
    private static Task Open(MultiProjectChecks.BrokerClient client, string uri, string text) => client.Notify("textDocument/didOpen", new JObject { ["textDocument"] = new JObject { ["uri"] = uri, ["languageId"] = "swift", ["version"] = 1, ["text"] = text } });
    private static Task Change(MultiProjectChecks.BrokerClient client, string uri, int version, JObject change) => client.Notify("textDocument/didChange", new JObject { ["textDocument"] = new JObject { ["uri"] = uri, ["version"] = version }, ["contentChanges"] = new JArray(change) });
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition, int timeout = 10000)
    {
        using var limit = new CancellationTokenSource(timeout);
        while (!condition()) await Task.Delay(10, limit.Token);
    }

    private sealed class Fixture
    {
        public string[] Roots { get; }
        public ConcurrentDictionary<string, FakeServer> Servers { get; } = new();
        public ConcurrentDictionary<string, int> Starts { get; } = new();
        public ConcurrentDictionary<string, (string State, string? Error)> States { get; } = new();
        public volatile bool HoldAlphaInitialization, FailAlphaStart;
        public Fixture(string name)
        {
            var directory = Path.GetFullPath(Path.Combine("artifacts", "broker-recovery", name + "-" + Guid.NewGuid().ToString("N")));
            Roots = new[] { Path.Combine(directory, "Alpha"), Path.Combine(directory, "Beta") };
            foreach (var root in Roots) Directory.CreateDirectory(root);
        }
        public SwiftLspBroker CreateBroker() => new(Roots, (root, token) =>
        {
            token.ThrowIfCancellationRequested();
            var count = Starts.AddOrUpdate(root, 1, (_, previous) => previous + 1);
            if (root == Roots[0] && FailAlphaStart) throw new IOException("Simulated process start failure.");
            var server = new FakeServer(root == Roots[0] && count > 1 && HoldAlphaInitialization);
            Servers[root] = server;
            return Task.FromResult(server.Endpoint);
        }, (root, state, error) => States[root] = (state, error));
    }

    private sealed class FakeServer
    {
        public SwiftMessageOutput Output { get; } = new();
        public SwiftLspEndpoint Endpoint { get; }
        public ConcurrentQueue<JObject> Notifications { get; } = new();
        public TaskCompletionSource<bool> DefinitionReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile int Callbacks;
        public volatile bool Disposed;
        private readonly bool hold;
        private JToken? initializeId;
        private readonly JObject documents = new();
        public FakeServer(bool hold)
        {
            this.hold = hold;
            Endpoint = new SwiftLspEndpoint(new SwiftMessageInput(Receive), Output, () => { Disposed = true; Output.Complete(); });
        }
        public void ReplyInitialize() => Reply(initializeId!, new JObject { ["capabilities"] = new JObject() });
        private void Reply(JToken id, JToken result) => Output.Enqueue(new JObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
        private Task Receive(JObject message)
        {
            var method = (string?)message["method"]; var id = message["id"];
            if (method == null)
            {
                Callbacks++;
                if (!hold) ReplyInitialize();
            }
            else if (method == "initialize")
            {
                initializeId = id!.DeepClone();
                Output.Enqueue(new JObject { ["jsonrpc"] = "2.0", ["id"] = 77, ["method"] = "workspace/configuration", ["params"] = new JObject { ["items"] = new JArray(new JObject()) } });
            }
            else if (method == "textDocument/definition") DefinitionReceived.TrySetResult(true);
            else if (id != null) Reply(id, documents.DeepClone());
            else
            {
                Notifications.Enqueue((JObject)message.DeepClone());
                if (method == "textDocument/didOpen") documents[(string)message["params"]!["textDocument"]!["uri"]!] = message["params"]!["textDocument"]!.DeepClone();
            }
            return Task.CompletedTask;
        }
    }
}
