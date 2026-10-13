using System.Text.Json.Nodes;

static class EditorFeatureChecks
{
    public static async Task<JsonObject> Run(LspSession rpc, string workspace)
    {
        var path = Path.Combine(workspace, "Sources", "LspSmoke", "EditorFeatures.swift");
        var text = await File.ReadAllTextAsync(path);
        var uri = new Uri(path).AbsoluteUri;
        await rpc.Notify("textDocument/didOpen", new { textDocument = new { uri, languageId = "swift", version = 1, text } });
        object Position(string marker, int offset = 0)
        {
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) throw new InvalidOperationException("Editor feature marker missing: " + marker);
            index += offset;
            var prefix = text[..index];
            return new { line = prefix.Count(c => c == '\n'), character = index - prefix.LastIndexOf('\n') - 1 };
        }
        var report = new JsonObject();
        var signature = await rpc.Request("textDocument/signatureHelp", new { textDocument = new { uri }, position = Position("\"Swift\", count:", 9) });
        if (signature?["signatures"] is not JsonArray signatures || !signatures.Any(s => s?["parameters"] is JsonArray { Count: 2 }))
            throw new InvalidOperationException("Signature Help did not return two method parameters.");
        report["signatureHelp"] = signature.DeepClone();
        var type = await rpc.Request("textDocument/typeDefinition", new { textDocument = new { uri }, position = Position("provider =", 1) });
        if (type is not JsonArray { Count: > 0 } || !type.ToJsonString().Contains("EditorFeatures.swift", StringComparison.Ordinal))
            throw new InvalidOperationException("Go To Type Definition missed the inferred provider type.");
        report["typeDefinition"] = type.DeepClone();
        JsonNode? implementation = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            implementation = await rpc.Request("textDocument/implementation", new { textDocument = new { uri }, position = Position("MessageProvider", 1) });
            if (implementation is JsonArray { Count: > 0 }) break;
            await Task.Delay(500);
        }
        if (implementation is not JsonArray { Count: > 0 }) throw new InvalidOperationException("Go To Implementation missed the conforming type.");
        report["implementation"] = implementation.DeepClone();
        var hints = await rpc.Request("textDocument/inlayHint", new { textDocument = new { uri }, range = new { start = new { line = 0, character = 0 }, end = new { line = text.Count(c => c == '\n'), character = 0 } } });
        if (hints is not JsonArray hintArray || !hintArray.Any(h => h?["kind"]?.GetValue<int>() == 1))
            throw new InvalidOperationException("Inlay Hints did not include inferred types.");
        report["inlayHints"] = hints.DeepClone();
        report["parameterHintsProvided"] = hintArray.Any(h => h?["kind"]?.GetValue<int>() == 2);
        await rpc.Notify("textDocument/didClose", new { textDocument = new { uri } });
        return report;
    }
}
