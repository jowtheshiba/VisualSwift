using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace VisualSwift
{
    internal sealed class SwiftProjectProfile
    {
        public string Optimization { get; set; } = "Default";
        public bool CxxInteroperability { get; set; }
        public string[] SwiftFlags { get; set; } = Array.Empty<string>();
        public string[] CFlags { get; set; } = Array.Empty<string>();
        public string[] CxxFlags { get; set; } = Array.Empty<string>();
        public string[] LinkerFlags { get; set; } = Array.Empty<string>();
        public string[] Conditions { get; set; } = Array.Empty<string>();
        public string Product { get; set; }
        public string[] Arguments { get; set; } = Array.Empty<string>();
        public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string WorkingDirectory { get; set; }
        public bool BuildBeforeLaunch { get; set; } = true;
        public bool UseF5 { get; set; }
        public bool StopOnEntry { get; set; }

        public void Validate()
        {
            if (!new[] { "Default", "-Onone", "-O", "-Osize" }.Contains(Optimization)) throw new InvalidOperationException("Select a valid optimization mode.");
            foreach (var values in new[] { SwiftFlags, CFlags, CxxFlags, LinkerFlags, Arguments, Conditions })
                if (values == null || values.Any(value => value == null || value.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)) throw new InvalidOperationException("Enter one argument per line.");
            if (Conditions.Any(value => !System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$"))) throw new InvalidOperationException("Compilation conditions must be identifiers such as DEBUG or PRODUCTION.");
            if (Environment == null || Environment.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.IndexOfAny(new[] { '=', '\0', '\r', '\n' }) >= 0 || pair.Value == null || pair.Value.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)) throw new InvalidOperationException("Enter environment variables as NAME=value, one per line.");
            if (UseF5 && string.IsNullOrWhiteSpace(Product)) throw new InvalidOperationException("Select an executable product to enable Swift debugging.");
        }
        public string BuildArguments(string configuration, string configurationOption = "-c")
        {
            Validate();
            var arguments = new List<string> { configurationOption, configuration.Equals("Release", StringComparison.OrdinalIgnoreCase) ? "release" : "debug" };
            if (Optimization != "Default") { arguments.Add("-Xswiftc"); arguments.Add(Optimization); }
            if (CxxInteroperability) { arguments.Add("-Xswiftc"); arguments.Add("-cxx-interoperability-mode=default"); }
            foreach (var condition in Conditions) { arguments.Add("-Xswiftc"); arguments.Add("-D" + condition); }
            AddFlags(arguments, "-Xswiftc", SwiftFlags);
            AddFlags(arguments, "-Xcc", CFlags);
            AddFlags(arguments, "-Xcxx", CxxFlags);
            AddFlags(arguments, "-Xlinker", LinkerFlags);
            return string.Join(" ", arguments.Select(SwiftPackages.Quote));
        }
        private static void AddFlags(List<string> arguments, string option, string[] flags)
        {
            foreach (var flag in flags) { arguments.Add(option); arguments.Add(flag); }
        }
        public string ResolveWorkingDirectory(string workspace)
        {
            var directory = string.IsNullOrWhiteSpace(WorkingDirectory) ? workspace : Path.GetFullPath(Path.Combine(workspace, WorkingDirectory));
            if (!Directory.Exists(directory)) throw new InvalidOperationException("The working directory does not exist: " + directory);
            return directory;
        }
    }
    internal sealed class SwiftProjectSettings
    {
        public SwiftProjectProfile Debug { get; set; } = new SwiftProjectProfile();
        public SwiftProjectProfile Release { get; set; } = new SwiftProjectProfile();
        public SwiftProjectProfile Profile(string configuration) => string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase) ? Release : Debug;
        public static string FilePath(string workspace) => Path.Combine(workspace, ".vs", "VisualSwift", "settings.json");
        public static bool Exists(string workspace) => File.Exists(FilePath(workspace)) && JObject.Parse(File.ReadAllText(FilePath(workspace)))["projectProfiles"] is JObject;
        public static SwiftProjectSettings Read(string workspace)
        {
            var document = File.Exists(FilePath(workspace)) ? JObject.Parse(File.ReadAllText(FilePath(workspace))) : new JObject();
            var settings = document["projectProfiles"]?.ToObject<SwiftProjectSettings>();
            if (settings != null) return settings;
            var path = SwiftDebugOptions.FilePath(workspace);
            var legacy = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)).ToObject<SwiftDebugOptions>() : new SwiftDebugOptions();
            return new SwiftProjectSettings
            {
                Debug = FromLegacy(legacy), Release = FromLegacy(legacy)
            };
        }
        private static SwiftProjectProfile FromLegacy(SwiftDebugOptions legacy) => new SwiftProjectProfile { Product = legacy.Product, Arguments = legacy.Arguments, UseF5 = legacy.UseF5, StopOnEntry = legacy.StopOnEntry };
        public void Save(string workspace)
        {
            Debug.Validate(); Release.Validate();
            SwiftSettings.Default.SaveProjectProfiles(workspace, JObject.FromObject(this));
        }
    }
}
