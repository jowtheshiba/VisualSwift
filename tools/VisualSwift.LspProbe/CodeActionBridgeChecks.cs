using Newtonsoft.Json.Linq;
using VisualSwift;

static class CodeActionBridgeChecks
{
    public static async Task Run()
    {
        const string uri = "file:///C:/CodeActions/A.swift";
        var bridge = new SwiftCodeActions();
        var diagnostic = JObject.Parse("{\"message\":\"Use let\",\"range\":{\"start\":{\"line\":1,\"character\":8},\"end\":{\"line\":1,\"character\":8}},\"codeActions\":[{\"title\":\"Use let\",\"kind\":\"quickfix\"}]}");
        bridge.Notification("textDocument/didOpen", new JObject { ["textDocument"] = new JObject { ["uri"] = uri, ["version"] = 1 } });
        bridge.Notification("textDocument/publishDiagnostics", new JObject { ["uri"] = uri, ["version"] = 1, ["diagnostics"] = new JArray(diagnostic) });
        var request = new JObject { ["textDocument"] = new JObject { ["uri"] = uri }, ["range"] = JObject.Parse("{\"start\":{\"line\":1,\"character\":4},\"end\":{\"line\":1,\"character\":4}}"), ["context"] = new JObject { ["diagnostics"] = new JArray(), ["only"] = new JArray("quickfix") } };
        if (bridge.Prepare(request)["context"]!["diagnostics"]![0]!["codeActions"] == null || ((JArray)request["context"]!["diagnostics"]!).Count != 0) throw new Exception("Fix-it metadata was not preserved without modifying the native request.");
        var other = (JObject)request.DeepClone(); other["textDocument"]!["uri"] = "file:///C:/CodeActions/B.swift";
        if (((JArray)bridge.Prepare(other)["context"]!["diagnostics"]!).Count != 0) throw new Exception("Diagnostics leaked across documents.");
        bridge.Notification("textDocument/didChange", new JObject { ["textDocument"] = new JObject { ["uri"] = uri, ["version"] = 2 } });
        bridge.Notification("textDocument/publishDiagnostics", new JObject { ["uri"] = uri, ["version"] = 1, ["diagnostics"] = new JArray(diagnostic) });
        if (((JArray)bridge.Prepare(request)["context"]!["diagnostics"]!).Count != 0) throw new Exception("An outdated diagnostic supplied a stale Fix-it.");
        var edit = JObject.Parse("{\"documentChanges\":[{\"textDocument\":{\"uri\":\"file:///C:/CodeActions/A.swift\",\"version\":2},\"edits\":[{\"range\":{\"start\":{\"line\":1,\"character\":4},\"end\":{\"line\":1,\"character\":7}},\"newText\":\"let\"}]},{\"textDocument\":{\"uri\":\"file:///C:/CodeActions/B.swift\",\"version\":null},\"edits\":[]}]}");
        bridge.ResolveAsync = action => Task.FromResult<JToken>(new JObject { ["title"] = action["title"]!.DeepClone(), ["edit"] = edit.DeepClone(), ["isPreferred"] = true });
        var command = new JObject { ["title"] = "Command action", ["command"] = "semantic.refactor.command", ["arguments"] = new JArray("payload") };
        var completed = (JArray)await bridge.CompleteAsync(new JArray(command, new JObject { ["title"] = "Disabled", ["disabled"] = new JObject { ["reason"] = "Unavailable" } }, new JObject { ["title"] = "Lazy action", ["data"] = new JObject { ["id"] = 1 } }));
        if (completed.Count != 2 || !JToken.DeepEquals(completed[0]["edit"], edit) || !JToken.DeepEquals(completed[1], command)) throw new Exception("Deferred resolution changed a multi-file edit or command action.");
        bridge.ResolveAsync = _ => Task.FromException<JToken>(new InvalidOperationException("Resolve failed"));
        completed = (JArray)await bridge.CompleteAsync(new JArray(new JObject { ["title"] = "Lazy failure", ["data"] = 1 }, command));
        if (completed.Count != 1 || !JToken.DeepEquals(completed[0], command)) throw new Exception("One failed resolution removed unrelated actions.");
        bridge.Clear();
    }
}
