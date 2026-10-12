using System.Diagnostics;
using System.Text.Json.Nodes;

static class RenameChecks
{
    public static async Task<JsonArray> Run(LspSession rpc, string workspace, string reportPath)
    {
        var files = Directory.GetFiles(Path.Combine(workspace, "Sources", "LspSmoke"), "*.swift").ToDictionary(p => new Uri(p).AbsoluteUri, File.ReadAllText);
        var fixture = files.First(p => p.Key.EndsWith("/RenameFixture.swift", StringComparison.Ordinal));
        await rpc.Notify("textDocument/didOpen", new { textDocument = new { uri = fixture.Key, languageId = "swift", version = 1, text = fixture.Value } });
        var cases = new[] {
            ("class", fixture.Key, "class DeliveryService", 6, "DeliveryService", "ParcelService", 3, 2),
            ("method-overload", fixture.Key, "func deliver(message", 5, "deliver", "dispatch(message:)", 3, 2),
            ("property", fixture.Key, "var completedCount", 4, "completedCount", "deliveredCount", 2, 1),
            ("local-shadowing", fixture.Key, "let receipt", 4, "receipt", "deliveryReceipt", 2, 1),
            ("struct", new Uri(Path.Combine(workspace, "Sources", "LspSmoke", "main.swift")).AbsoluteUri, "struct Greeter", 7, "Greeter", "Welcomer", 3, 2),
            ("variable", new Uri(Path.Combine(workspace, "Sources", "LspSmoke", "main.swift")).AbsoluteUri, "let greeter", 4, "greeter", "welcomer", 2, 1)
        };
        var result = new JsonArray();
        foreach (var (kind, uri, marker, offset, oldName, newName, count, fileCount) in cases)
        {
            var source = files[uri];
            var index = source.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) throw new InvalidOperationException("Rename marker missing: " + marker);
            index += offset;
            var prefix = source[..index];
            var position = new { line = prefix.Count(c => c == '\n'), character = index - (prefix.LastIndexOf('\n') + 1) };
            var prepared = await rpc.Request("textDocument/prepareRename", new { textDocument = new { uri }, position });
            if (prepared["range"] == null && prepared["start"] == null) throw new InvalidOperationException("Prepare Rename returned no symbol range: " + kind);
            JsonNode? edits = null;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                edits = await rpc.Request("textDocument/rename", new { textDocument = new { uri }, position, newName });
                if (edits["changes"] is JsonObject changes && changes.Count == fileCount && changes.Sum(p => p.Value!.AsArray().Count) == count) break;
                await Task.Delay(500);
            }
            var actual = edits?["changes"] as JsonObject ?? throw new InvalidOperationException("Rename returned no edits: " + kind);
            if (actual.Count != fileCount || actual.Sum(p => p.Value!.AsArray().Count) != count) throw new InvalidOperationException("Rename missed references or changed unrelated symbols: " + kind);
            var renamed = new Dictionary<string, string>(files);
            foreach (var (fileUri, replacements) in actual)
            {
                if (!files.TryGetValue(fileUri, out var text)) throw new InvalidOperationException("Unexpected rename target: " + fileUri);
                int Position(JsonNode p)
                {
                    var line = p["line"]!.GetValue<int>();
                    var start = 0;
                    for (var n = 0; n < line; n++) { start = text.IndexOf('\n', start) + 1; if (start == 0) throw new InvalidOperationException("Invalid rename line."); }
                    return start + p["character"]!.GetValue<int>();
                }
                var spans = replacements!.AsArray().Select(e => (start: Position(e!["range"]!["start"]!), end: Position(e["range"]!["end"]!), value: e["newText"]!.GetValue<string>())).OrderByDescending(e => e.start).ToArray();
                var boundary = text.Length;
                foreach (var edit in spans)
                {
                    if (edit.start < 0 || edit.end > boundary || text[edit.start..edit.end] != oldName) throw new InvalidOperationException("Rename edit touches unexpected text: " + kind);
                    text = text[..edit.start] + edit.value + text[edit.end..];
                    boundary = edit.start;
                }
                renamed[fileUri] = text;
            }
            if (!renamed[fixture.Key].Contains("\"DeliveryService completedCount receipt\"", StringComparison.Ordinal) || !renamed[fixture.Key].Contains("let receipt = \"unchanged\"", StringComparison.Ordinal) || !renamed[fixture.Key].Contains("func deliver(_ amount: Int)", StringComparison.Ordinal)) throw new InvalidOperationException("Rename changed a string, shadowed local, or overload.");
            var output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "rename-checks", kind);
            Directory.CreateDirectory(output);
            var compiler = Path.Combine(Path.GetDirectoryName(rpc.Executable)!, "swiftc.exe");
            var info = new ProcessStartInfo(compiler) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-typecheck"); info.ArgumentList.Add("-swift-version"); info.ArgumentList.Add("6");
            foreach (var file in renamed) { var path = Path.Combine(output, Path.GetFileName(new Uri(file.Key).LocalPath)); File.WriteAllText(path, file.Value); info.ArgumentList.Add(path); }
            using var process = Process.Start(info)!;
            var errors = process.StandardError.ReadToEndAsync();
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            if (process.ExitCode != 0) throw new InvalidOperationException("Renamed Swift code did not typecheck: " + kind + " " + await errors);
            await standardOutput;
            result.Add(new JsonObject { ["kind"] = kind, ["editCount"] = count, ["fileCount"] = fileCount, ["typechecked"] = true, ["prepared"] = prepared.DeepClone(), ["edit"] = edits!.DeepClone() });
        }
        await rpc.Notify("textDocument/didClose", new { textDocument = new { uri = fixture.Key } });
        return result;
    }
}
