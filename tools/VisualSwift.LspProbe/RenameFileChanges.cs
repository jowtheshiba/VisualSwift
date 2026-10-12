using System.Text.Json.Nodes;
using Newtonsoft.Json.Linq;

static class RenameFileChanges
{
    public static async Task<JsonArray> Run(LspSession rpc, string workspace)
    {
        var directory = Path.Combine(workspace, "Sources", "LspSmoke");
        var added = Path.Combine(directory, "DynamicRenameFixture.swift");
        var moved = Path.Combine(directory, "MovedRenameFixture.swift");
        if (File.Exists(added) || File.Exists(moved)) throw new InvalidOperationException("Dynamic rename fixtures already exist.");
        var events = new List<JObject>();
        using var watcher = new VisualSwift.SwiftFileWatcher(workspace, async changes =>
        {
            lock (events) events.Add((JObject)changes.DeepClone());
            await rpc.Notify("workspace/didChangeWatchedFiles", JsonNode.Parse(changes.ToString(Newtonsoft.Json.Formatting.None)));
        });
        var fixtureUri = new Uri(Path.Combine(directory, "RenameFixture.swift")).AbsoluteUri;
        await rpc.Notify("textDocument/didOpen", new { textDocument = new { uri = fixtureUri, languageId = "swift", version = 1, text = await File.ReadAllTextAsync(Path.Combine(directory, "RenameFixture.swift")) } });
        var report = new JsonArray();
        async Task Check(string phase, int count, string? expectedUri, int eventType, string eventPath)
        {
            var eventUri = new Uri(eventPath).AbsoluteUri;
            var notified = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await watcher.FlushAsync();
                lock (events) notified = events.SelectMany(e => (JArray)e["changes"]!).Any(e => (string?)e["uri"] == eventUri && (int?)e["type"] == eventType);
                if (notified) break;
                await Task.Delay(100);
            }
            if (!notified) throw new InvalidOperationException("File event was not forwarded: " + phase);
            await rpc.Request("workspace/synchronize", new { index = true });
            var edit = await rpc.Request("textDocument/rename", new { textDocument = new { uri = fixtureUri }, position = new { line = 0, character = 15 }, newName = "ParcelService" });
            var changes = edit["changes"]!.AsObject();
            if (changes.Sum(p => p.Value!.AsArray().Count) != count || (expectedUri != null && !changes.ContainsKey(expectedUri))) throw new InvalidOperationException("Rename has stale file references after " + phase + ": " + edit);
            if ((phase == "moved" && changes.ContainsKey(new Uri(added).AbsoluteUri)) || (phase == "deleted" && changes.ContainsKey(new Uri(moved).AbsoluteUri))) throw new InvalidOperationException("Rename includes removed source file: " + phase);
            report.Add(new JsonObject { ["phase"] = phase, ["editCount"] = count, ["eventType"] = eventType, ["passed"] = true });
            lock (events) events.Clear();
        }
        try
        {
            await watcher.FlushAsync();
            lock (events) events.Clear();
            await File.WriteAllTextAsync(added, "func dynamicDelivery(_ service: DeliveryService) {}\n");
            await Check("created", 4, new Uri(added).AbsoluteUri, 1, added);
            await File.WriteAllTextAsync(added, "func dynamicDelivery(_ service: DeliveryService) { _ = DeliveryService() }\n");
            await Check("changed", 5, new Uri(added).AbsoluteUri, 2, added);
            File.Move(added, moved);
            await Check("moved", 5, new Uri(moved).AbsoluteUri, 1, moved);
            File.Delete(moved);
            await Check("deleted", 3, null, 3, moved);
            return report;
        }
        finally
        {
            if (File.Exists(added)) File.Delete(added);
            if (File.Exists(moved)) File.Delete(moved);
            await Task.Delay(250);
            await watcher.FlushAsync();
            await rpc.Notify("textDocument/didClose", new { textDocument = new { uri = fixtureUri } });
        }
    }
}
