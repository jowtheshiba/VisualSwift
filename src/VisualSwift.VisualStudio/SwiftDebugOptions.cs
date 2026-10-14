using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace VisualSwift
{
    internal sealed class SwiftDebugOptions
    {
        public string Product { get; set; }
        public string[] Arguments { get; set; } = new string[0];
        public bool StopOnEntry { get; set; }
        public bool UseF5 { get; set; }
        public static string FilePath(string workspace) => Path.Combine(workspace, ".vs", "VisualSwift", "debug.json");
        public static SwiftDebugOptions Read(string workspace, string configuration = "Debug")
        {
            if (workspace != null && SwiftProjectSettings.Exists(workspace))
            {
                var profile = SwiftProjectSettings.Read(workspace).Profile(configuration);
                return new SwiftDebugOptions { Product = profile.Product, Arguments = profile.Arguments, UseF5 = profile.UseF5, StopOnEntry = profile.StopOnEntry };
            }
            if (workspace == null || !File.Exists(FilePath(workspace))) return new SwiftDebugOptions();
            var options = JObject.Parse(File.ReadAllText(FilePath(workspace))).ToObject<SwiftDebugOptions>();
            if (options.Arguments == null) throw new InvalidOperationException("Swift debug arguments must be an array of strings.");
            return options;
        }
        public void Save(string workspace)
        {
            if (workspace == null) throw new InvalidOperationException("Open a Swift package first.");
            if (SwiftProjectSettings.Exists(workspace))
            {
                var settings = SwiftProjectSettings.Read(workspace);
                settings.Debug.Product = Product; settings.Debug.Arguments = Arguments; settings.Debug.UseF5 = UseF5; settings.Debug.StopOnEntry = StopOnEntry;
                settings.Save(workspace);
                return;
            }
            var path = FilePath(workspace);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JObject.FromObject(this).ToString());
        }
    }

}
