#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace VisualSwift
{
    internal sealed class SwiftCodeActions
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, JArray> diagnostics = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> versions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Func<JToken, Task<JToken>> ResolveAsync { get; set; }

        public static void AdaptCapabilities(JObject textDocument)
        {
            if (textDocument == null) return;
            var action = textDocument["codeAction"] as JObject;
            if (action == null) return;
            action["dataSupport"] = true;
            action["resolveSupport"] = new JObject { ["properties"] = new JArray("edit", "command") };
        }
        public void Notification(string method, JToken parameters)
        {
            var uri = (string)(parameters?["uri"] ?? parameters?["textDocument"]?["uri"]);
            if (uri == null) return;
            lock (gate)
            {
                if (method == "textDocument/publishDiagnostics")
                {
                    var version = (int?)parameters?["version"];
                    if (version != null && versions.TryGetValue(uri, out var current) && version < current) return;
                    diagnostics[uri] = (JArray)(parameters?["diagnostics"] as JArray ?? new JArray()).DeepClone();
                }
                else if (method == "textDocument/didChange" || method == "textDocument/didOpen")
                {
                    diagnostics.Remove(uri);
                    var version = (int?)parameters?["textDocument"]?["version"];
                    if (version != null) versions[uri] = version.Value;
                }
                else if (method == "textDocument/didClose") { diagnostics.Remove(uri); versions.Remove(uri); }
            }
        }
        public JToken Prepare(JToken parameters)
        {
            var result = parameters?.DeepClone();
            if (!(result is JObject request) || !(request["context"] is JObject context)) return result;
            var uri = (string)request["textDocument"]?["uri"];
            JArray cached;
            lock (gate) cached = uri != null && diagnostics.TryGetValue(uri, out var items) ? (JArray)items.DeepClone() : new JArray();
            var supplied = context["diagnostics"] as JArray ?? new JArray();
            var merged = new JArray();
            foreach (var diagnostic in supplied)
            {
                var original = cached.FirstOrDefault(item => (string)item["message"] == (string)diagnostic["message"] && JToken.DeepEquals(item["range"], diagnostic["range"]));
                merged.Add((original ?? diagnostic).DeepClone());
            }
            foreach (var diagnostic in cached.Where(item => Intersects(item["range"], request["range"])))
                if (!merged.Any(item => (string)item["message"] == (string)diagnostic["message"] && JToken.DeepEquals(item["range"], diagnostic["range"]))) merged.Add(diagnostic.DeepClone());
            context["diagnostics"] = merged;
            return result;
        }
        private static bool Intersects(JToken diagnostic, JToken range)
        {
            if (diagnostic == null || range == null) return false;
            var start = range["start"]; var end = range["end"];
            if (JToken.DeepEquals(start, end)) return (int?)start?["line"] >= (int?)diagnostic["start"]?["line"] && (int?)start?["line"] <= (int?)diagnostic["end"]?["line"];
            return Compare(diagnostic["start"], end) <= 0 && Compare(diagnostic["end"], start) >= 0;
        }
        private static int Compare(JToken left, JToken right)
        {
            var line = ((int?)left?["line"] ?? 0).CompareTo((int?)right?["line"] ?? 0);
            return line != 0 ? line : ((int?)left?["character"] ?? 0).CompareTo((int?)right?["character"] ?? 0);
        }
        public async Task<JToken> CompleteAsync(JToken response)
        {
            if (!(response is JArray actions)) return response;
            var result = new JArray();
            foreach (var item in actions)
            {
                if (item["disabled"] != null && item["disabled"].Type != JTokenType.Null) continue;
                var action = item.DeepClone();
                if ((action["edit"] == null || action["edit"].Type == JTokenType.Null) && (action["command"] == null || action["command"].Type == JTokenType.Null) && action["data"] != null && ResolveAsync != null)
                {
                    try { action = await ResolveAsync(action).ConfigureAwait(false); }
                    catch (Exception error) { PrototypeLog.Write("Swift code action resolution failed: " + error.Message); continue; }
                }
                if (!(action is JObject) || (action["disabled"] != null && action["disabled"].Type != JTokenType.Null)) continue;
                result.Add(action);
            }
            return new JArray(result.OrderByDescending(action => (bool?)action["isPreferred"] == true).Select(action => action.DeepClone()));
        }
        public void Clear()
        {
            lock (gate) { diagnostics.Clear(); versions.Clear(); }
            ResolveAsync = null;
        }
    }
}
