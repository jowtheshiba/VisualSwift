using System;
using System.IO;
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

        private static string WorkspacePath(string workspace) => Path.Combine(workspace, ".vs", "VisualSwift", "settings.json");
        private static string ReadPath(string path)
        {
            if (!File.Exists(path)) return null;
            var value = JObject.Parse(File.ReadAllText(path)).Value<string>("toolchain");
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static void Write(string path, string value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var document = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
            document["toolchain"] = value;
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

    internal sealed class ToolchainSelection
    {
        public ToolchainSelection(string path, string scope) { Path = path; Scope = scope; }
        public string Path { get; }
        public string Scope { get; }
    }
}
