using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace VisualSwift
{
    internal sealed class SwiftSettings
    {
        private readonly object gate = new object();
        private readonly string globalPath;
        public static SwiftSettings Default { get; } = new SwiftSettings(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisualSwift", "settings.json"));
        public SwiftSettings(string globalPath) { this.globalPath = globalPath; }

        public ToolchainSelection Read(string workspace)
        {
            lock (gate)
            {
                var local = workspace == null ? null : ReadPath(WorkspacePath(workspace));
                if (local != null) return new ToolchainSelection(local, "Workspace");
                var global = ReadPath(globalPath);
                if (global != null) return new ToolchainSelection(global, "Global");
                var environment = Environment.GetEnvironmentVariable("VISUALSWIFT_TOOLCHAIN");
                return new ToolchainSelection(string.IsNullOrWhiteSpace(environment) ? null : environment, string.IsNullOrWhiteSpace(environment) ? "PATH" : "Environment");
            }
        }

        public void Save(string toolchain, string workspace, bool workspaceScope)
        {
            if (workspaceScope && workspace == null) throw new InvalidOperationException("Open a Swift package before saving workspace settings.");
            lock (gate) Write(workspaceScope ? WorkspacePath(workspace) : globalPath, toolchain);
        }

        public void ClearWorkspace(string workspace)
        {
            if (workspace == null) throw new InvalidOperationException("No Swift package is open.");
            lock (gate) Write(WorkspacePath(workspace), null);
        }

        public InlayHintSettings ReadInlayHints(string workspace)
        {
            lock (gate)
            {
                var global = ReadDocument(globalPath)["inlayHints"] as JObject;
                var local = workspace == null ? null : ReadDocument(WorkspacePath(workspace))["inlayHints"] as JObject;
                return new InlayHintSettings(local?.Value<bool?>("enabled") ?? global?.Value<bool?>("enabled") ?? true,
                    local?.Value<bool?>("types") ?? global?.Value<bool?>("types") ?? true,
                    local?.Value<bool?>("parameters") ?? global?.Value<bool?>("parameters") ?? true,
                    local == null ? "Global" : "Workspace");
            }
        }

        public void SaveInlayHints(InlayHintSettings hints, string workspace, bool workspaceScope)
        {
            if (workspaceScope && workspace == null) throw new InvalidOperationException("Open a Swift package before saving workspace settings.");
            lock (gate) WriteProperty(workspaceScope ? WorkspacePath(workspace) : globalPath, "inlayHints", new JObject
            {
                ["enabled"] = hints.Enabled, ["types"] = hints.Types, ["parameters"] = hints.Parameters
            });
        }

        public void ClearWorkspaceInlayHints(string workspace)
        {
            if (workspace == null) throw new InvalidOperationException("No Swift package is open.");
            lock (gate) WriteProperty(WorkspacePath(workspace), "inlayHints", null);
        }

        private static JObject ReadDocument(string path) => File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();

        private static string WorkspacePath(string workspace) => Path.Combine(workspace, ".vs", "VisualSwift", "settings.json");
        private static string ReadPath(string path)
        {
            if (!File.Exists(path)) return null;
            var value = JObject.Parse(File.ReadAllText(path)).Value<string>("toolchain");
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static void Write(string path, string value)
            => WriteProperty(path, "toolchain", value == null ? null : JToken.FromObject(value));

        private static void WriteProperty(string path, string property, JToken value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var document = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
            document[property] = value;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, document.ToString());
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal sealed class InlayHintSettings
    {
        public InlayHintSettings(bool enabled, bool types, bool parameters, string scope = "Global")
        { Enabled = enabled; Types = types; Parameters = parameters; Scope = scope; }
        public bool Enabled { get; }
        public bool Types { get; }
        public bool Parameters { get; }
        public string Scope { get; }
        public JArray Filter(JArray hints) => new JArray(hints.Where(hint => Enabled &&
            (hint.Value<int?>("kind") == 1 ? Types : hint.Value<int?>("kind") == 2 ? Parameters : true)));
    }

    internal sealed class ToolchainSelection
    {
        public ToolchainSelection(string path, string scope) { Path = path; Scope = scope; }
        public string Path { get; }
        public string Scope { get; }
    }
}
