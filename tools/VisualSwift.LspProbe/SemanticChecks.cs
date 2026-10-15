using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

static class SemanticChecks
{
    public static async Task Run(string server, string workspace, string reportPath)
    {
        await ProtocolChecks.Run();
        workspace = Path.GetFullPath(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "Sources", "App"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "Package.swift"), "// swift-tools-version: 6.0\nimport PackageDescription\nlet package = Package(name: \"SemanticSmoke\", targets: [.executableTarget(name: \"App\")])\n");
        var source = """
protocol Displayable { func describe() -> String }
struct Record: Displayable {
    let identifier: Int
    var title: String
    static let category = "Record"
    func describe() -> String { title }
    func instanceMethod(parameterValue: Int) -> Int {
        let localConstant = parameterValue + identifier
        var localVariable = localConstant
        localVariable += 1
        return localVariable
    }
    static func staticMethod() -> String { category }
}
class BaseClass {}
final class DerivedClass: BaseClass {}
enum Status { case ready, failed }
actor Counter { var count = 0; func increment() { count += 1 } }
typealias RecordAlias = Record
func identity<Element>(_ element: Element) -> Element { element }
func globalFunction(_ value: Int) -> Int { value + 1 }
func inspect(_ display: any Displayable) -> String { display.describe() }
@available(*, deprecated, message: "Use globalFunction")
func oldFunction() -> Int { 0 }
func exercise() {
    var record = Record(identifier: 1, title: "Hello")
    record.title = "World"
    let propertyValue = record.identifier
    let methodResult = record.instanceMethod(parameterValue: propertyValue)
    let staticProperty = Record.category
    let staticResult = Record.staticMethod()
    let status = Status.ready
    let instance = DerivedClass()
    let alias: RecordAlias = record
    let genericResult = identity(methodResult)
    let globalResult = globalFunction(genericResult)
    let deprecatedResult = oldFunction()
    let actorInstance = Counter()
    print(record.describe(), staticProperty, staticResult, status, instance, alias, globalResult, deprecatedResult, actorInstance)
}
exercise()
""" + "\n";
        var sourcePath = Path.Combine(workspace, "Sources", "App", "main.swift");
        await File.WriteAllTextAsync(sourcePath, source);
        var report = new JsonObject();
        try
        {
            using (var build = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(server)!, "swift.exe"), "build --package-path \"" + workspace + "\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!)
            {
                var output = build.StandardOutput.ReadToEndAsync(); var error = build.StandardError.ReadToEndAsync();
                await build.WaitForExitAsync(); report["buildOutput"] = await output + await error;
                if (build.ExitCode != 0) throw new InvalidOperationException("Semantic fixture build failed.");
            }
            await using var rpc = new LspSession(server, workspace);
            var types = new[] { "namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter", "variable", "property", "enumMember", "event", "function", "method", "macro", "keyword", "modifier", "comment", "string", "number", "regexp", "operator" };
            var modifiers = new[] { "declaration", "definition", "readonly", "static", "deprecated", "abstract", "async", "modification", "documentation", "defaultLibrary" };
            var initialized = await rpc.Request("initialize", new { processId = Environment.ProcessId, rootUri = new Uri(workspace + Path.DirectorySeparatorChar).AbsoluteUri, capabilities = new { general = new { positionEncodings = new[] { "utf-16" } }, textDocument = new { semanticTokens = new { requests = new { full = true, range = true }, tokenTypes = types, tokenModifiers = modifiers, formats = new[] { "relative" } } } } });
            var legend = initialized["capabilities"]!["semanticTokensProvider"]!["legend"]!;
            report["legend"] = legend.DeepClone();
            await rpc.Notify("initialized", new { });
            var uri = new Uri(sourcePath).AbsoluteUri;
            await rpc.Notify("textDocument/didOpen", new { textDocument = new { uri, languageId = "swift", version = 1, text = source } });
            JsonArray Decode(JsonNode result, string text)
            {
                var decoded = new JsonArray(); var data = result["data"]!.AsArray();
                var lines = text.Split('\n'); var line = 0; var character = 0;
                if (data.Count % 5 != 0) throw new InvalidOperationException("Invalid semantic token tuple count.");
                for (var i = 0; i < data.Count; i += 5)
                {
                    var deltaLine = data[i]!.GetValue<int>(); line += deltaLine;
                    character = deltaLine == 0 ? character + data[i + 1]!.GetValue<int>() : data[i + 1]!.GetValue<int>();
                    var length = data[i + 2]!.GetValue<int>(); var type = data[i + 3]!.GetValue<int>(); var bits = data[i + 4]!.GetValue<int>();
                    if (line >= lines.Length || character + length > lines[line].Length || type >= legend["tokenTypes"]!.AsArray().Count) throw new InvalidOperationException("Semantic token is outside the document or legend.");
                    var mods = new JsonArray();
                    for (var bit = 0; bit < legend["tokenModifiers"]!.AsArray().Count; bit++) if ((bits & (1 << bit)) != 0) mods.Add(legend["tokenModifiers"]![bit]!.ToString());
                    decoded.Add(new JsonObject { ["text"] = lines[line].Substring(character, length), ["line"] = line + 1, ["column"] = character + 1, ["type"] = legend["tokenTypes"]![type]!.ToString(), ["modifiers"] = mods });
                }
                return decoded;
            }
            JsonArray? tokens = null;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                await rpc.Request("textDocument/hover", new { textDocument = new { uri }, position = new { line = 28, character = 25 } });
                tokens = Decode(await rpc.Request("textDocument/semanticTokens/full", new { textDocument = new { uri } }), source);
                if (tokens.Any(token => token?["text"]?.ToString() == "instanceMethod")) break;
                await Task.Delay(500);
            }
            report["tokens"] = tokens!.DeepClone();
            report["categories"] = new JsonArray(tokens.Select(token => token!["type"]!.ToString()).Distinct().Order().Select(category => (JsonNode)JsonValue.Create(category)!).ToArray());
            report["modifiers"] = new JsonArray(tokens.SelectMany(token => token!["modifiers"]!.AsArray()).Select(mod => mod!.ToString()).Distinct().Order().Select(mod => (JsonNode)JsonValue.Create(mod)!).ToArray());
            if (tokens.Count == 0) throw new InvalidOperationException("No semantic tokens returned.");
            var range = await rpc.Request("textDocument/semanticTokens/range", new { textDocument = new { uri }, range = new { start = new { line = 25, character = 0 }, end = new { line = 39, character = 0 } } });
            report["rangeTokens"] = Decode(range, source);
            var changed = source.Replace("instanceMethod", "renamedMethod", StringComparison.Ordinal);
            await rpc.Notify("textDocument/didChange", new { textDocument = new { uri, version = 2 }, contentChanges = new[] { new { text = changed } } });
            var after = Decode(await rpc.Request("textDocument/semanticTokens/full", new { textDocument = new { uri } }), changed);
            report["afterEdit"] = after.DeepClone();
            if (!after.Any(token => token?["text"]?.ToString() == "renamedMethod") || after.Any(token => token?["text"]?.ToString() == "instanceMethod")) throw new InvalidOperationException("Semantic tokens did not refresh after edit.");
            report["semanticChecksPassed"] = true;
            report["passed"] = true;
            await rpc.Notify("textDocument/didClose", new { textDocument = new { uri } });
            await rpc.Request("shutdown", null); await rpc.Notify("exit", null); rpc.CloseInput(); await rpc.WaitForExit();
        }
        catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); Environment.ExitCode = 1; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
