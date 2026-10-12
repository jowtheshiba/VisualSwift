using System.Text.Json.Nodes;

static class FormattingChecks
{
    public static async Task<JsonObject> Run(LspSession rpc, string workspace, string reportPath)
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "format-checks");
        var nested = Path.Combine(directory, "nested");
        Directory.CreateDirectory(nested);
        var path = Path.Combine(nested, "Unicode 😀.swift");
        var uri = new Uri(path).AbsoluteUri;
        var config = Path.Combine(directory, ".swift-format");
        var nestedConfig = Path.Combine(nested, ".swift-format");
        if (File.Exists(config)) File.Delete(config);
        if (File.Exists(nestedConfig)) File.Delete(nestedConfig);
        var disk = "func diskVersion() {}\n";
        await File.WriteAllTextAsync(path, disk);
        var source = "func first(){\nlet emoji=\"😀\"\nprint(emoji)\n}\n\nfunc second(){let value=2;print(value)}\n";
        var version = 1;
        await rpc.Notify("textDocument/didOpen", new { textDocument = new { uri, languageId = "swift", version, text = source } });
        var report = new JsonObject();
        async Task Change(string text)
        {
            source = text;
            await rpc.Notify("textDocument/didChange", new { textDocument = new { uri, version = ++version }, contentChanges = new[] { new { text } } });
        }
        async Task<string> Format(bool selection = false, bool spaces = true, int size = 3)
        {
            object parameters = selection
                ? new { textDocument = new { uri }, options = new { tabSize = size, insertSpaces = spaces }, range = new { start = new { line = 0, character = 0 }, end = new { line = 3, character = 1 } } }
                : new { textDocument = new { uri }, options = new { tabSize = size, insertSpaces = spaces } };
            var edits = await rpc.Request(selection ? "textDocument/rangeFormatting" : "textDocument/formatting", parameters);
            return Apply(source, edits.AsArray());
        }
        try
        {
            var original = source;
            var document = await Format();
            if (!document.Contains("   let emoji = \"😀\"", StringComparison.Ordinal) || !document.Contains("func second() {", StringComparison.Ordinal) || document.Contains("diskVersion", StringComparison.Ordinal)) throw new InvalidOperationException("Document formatting ignored buffer text or indentation options.");
            report["unsavedDocument"] = true;
            await Change(document);
            if (await Format() != document) throw new InvalidOperationException("Formatting is not idempotent.");
            report["idempotent"] = true;
            await Change(original);
            var selection = await Format(selection: true);
            if (!selection.Contains("   let emoji = \"😀\"", StringComparison.Ordinal) || !selection.EndsWith("func second(){let value=2;print(value)}\n", StringComparison.Ordinal)) throw new InvalidOperationException("Selection formatting changed an unrelated function or missed the selection.");
            report["selectionAndUnicode"] = true;
            var tabs = await Format(spaces: false, size: 4);
            if (!tabs.Contains("\tlet emoji", StringComparison.Ordinal)) throw new InvalidOperationException("Formatting ignored tab indentation.");
            report["tabs"] = true;
            await File.WriteAllTextAsync(config, "{\"version\":1,\"indentation\":{\"spaces\":4}}");
            var configured = await Format(size: 2);
            if (!configured.Contains("    let emoji", StringComparison.Ordinal)) throw new InvalidOperationException("Parent .swift-format was ignored.");
            report["parentConfiguration"] = true;
            await File.WriteAllTextAsync(nestedConfig, "{\"version\":1,\"indentation\":{\"spaces\":2}}");
            var nearest = await Format(size: 4);
            if (!nearest.Contains("\n  let emoji", StringComparison.Ordinal)) throw new InvalidOperationException("Nearest .swift-format was ignored.");
            report["nearestConfiguration"] = true;
            await File.WriteAllTextAsync(nestedConfig, "invalid configuration");
            string? failure = null;
            try { await Format(); }
            catch (InvalidOperationException error) { failure = error.Message; }
            if (failure == null) throw new InvalidOperationException("Invalid configuration did not produce a formatting error.");
            report["configurationError"] = failure;
            File.Delete(nestedConfig);
            File.Delete(config);
            await Change(original.Replace("\n", "\r\n", StringComparison.Ordinal));
            var crlf = await Format();
            if (!crlf.Contains("😀", StringComparison.Ordinal) || !crlf.Contains("let emoji =", StringComparison.Ordinal)) throw new InvalidOperationException("CRLF or Unicode formatting failed.");
            report["crlf"] = true;
            await Change("func broken( {\n");
            failure = null;
            try { await Format(); }
            catch (InvalidOperationException error) { failure = error.Message; }
            if (failure == null) throw new InvalidOperationException("Invalid Swift syntax did not produce a formatting error.");
            report["syntaxError"] = failure;
            if (await File.ReadAllTextAsync(path) != disk) throw new InvalidOperationException("Formatting wrote to the file on disk.");
            report["diskUnchanged"] = true;
            var modern = await File.ReadAllTextAsync(Path.Combine(workspace, "Sources", "LspSmoke", "SyntaxFixture.swift"));
            await Change(modern);
            var formattedModern = await Format();
            if (!formattedModern.Contains("Swift::Int", StringComparison.Ordinal) || !formattedModern.Contains("@concurrent", StringComparison.Ordinal)) throw new InvalidOperationException("Modern Swift constructs were lost during formatting.");
            await File.WriteAllTextAsync(Path.Combine(directory, "modern-formatted.swift"), formattedModern);
            await File.WriteAllTextAsync(Path.Combine(directory, "document-formatted.swift"), document);
            await File.WriteAllTextAsync(Path.Combine(directory, "selection-formatted.swift"), selection);
            report["modernSwift"] = true;
            return report;
        }
        finally { await rpc.Notify("textDocument/didClose", new { textDocument = new { uri } }); }
    }

    private static string Apply(string source, JsonArray edits)
    {
        int Offset(JsonNode position)
        {
            var offset = 0;
            for (var line = 0; line < position["line"]!.GetValue<int>(); line++)
            {
                offset = source.IndexOf('\n', offset) + 1;
                if (offset == 0) throw new InvalidOperationException("Invalid formatting line.");
            }
            return offset + position["character"]!.GetValue<int>();
        }
        var replacements = edits.Select(edit => (start: Offset(edit!["range"]!["start"]!), end: Offset(edit["range"]!["end"]!), text: edit["newText"]!.GetValue<string>())).OrderByDescending(edit => edit.start).ToArray();
        var boundary = source.Length;
        foreach (var edit in replacements)
        {
            if (edit.start < 0 || edit.end < edit.start || edit.end > boundary) throw new InvalidOperationException("Formatting returned invalid or overlapping edits.");
            source = source[..edit.start] + edit.text + source[edit.end..];
            boundary = edit.start;
        }
        return source;
    }
}
