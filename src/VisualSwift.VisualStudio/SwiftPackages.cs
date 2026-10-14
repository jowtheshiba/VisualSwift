using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VisualSwift
{
    internal sealed class SwiftDependency
    {
        public string Identity { get; set; }
        public string Source { get; set; }
        public string Requirement { get; set; }
        public string Version { get; set; }
        public string Directory { get; set; }
        public string PackageName { get; set; }
        public string MinimumVersion { get; set; }
        public string[] Products { get; set; } = Array.Empty<string>();
        public bool Local { get; set; }
        public string Status => Local ? "Local" : string.IsNullOrEmpty(Directory) ? "Unresolved" : "Resolved";
    }

    internal sealed class SwiftPackageSnapshot
    {
        public string Name { get; set; }
        public string Workspace { get; set; }
        public string[] Targets { get; set; }
        public List<SwiftDependency> Dependencies { get; } = new List<SwiftDependency>();
    }

    internal sealed class SwiftPackageEdit
    {
        public string Workspace { get; set; }
        public string Original { get; set; }
        public string Updated { get; set; }
    }

    internal sealed class SwiftPackageRequest
    {
        public string Source { get; set; }
        public string Requirement { get; set; } = "From Version";
        public string Value { get; set; }
        public string Product { get; set; }
        public string Target { get; set; }
        public bool Local => Requirement == "Local Path";
    }

    internal static class SwiftPackages
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        public static bool Busy(string workspace) => Gates.TryGetValue(Path.GetFullPath(workspace), out var gate) && gate.CurrentCount == 0;

        public static async Task<SwiftPackageSnapshot> ReadAsync(string workspace, CancellationToken token)
        {
            workspace = Path.GetFullPath(workspace);
            var toolchain = await Task.Run(() => Toolchain.Resolve(workspace), token).ConfigureAwait(false);
            var manifest = JObject.Parse(await RunAsync(toolchain, workspace, "dump-package", token).ConfigureAwait(false));
            var result = new SwiftPackageSnapshot { Workspace = workspace, Name = (string)manifest["name"], Targets = (manifest["targets"] as JArray ?? new JArray()).Select(target => (string)target["name"]).ToArray() };
            var statePath = Path.Combine(workspace, ".build", "workspace-state.json");
            var state = File.Exists(statePath) ? JObject.Parse(File.ReadAllText(statePath)) : new JObject();
            var resolvedPath = Path.Combine(workspace, "Package.resolved");
            var resolved = File.Exists(resolvedPath) ? JObject.Parse(File.ReadAllText(resolvedPath)) : new JObject();
            var pins = resolved["pins"] as JArray ?? resolved["object"]?["pins"] as JArray ?? new JArray();
            foreach (var dependency in manifest["dependencies"] as JArray ?? new JArray())
            {
                var local = dependency["fileSystem"]?[0];
                var remote = dependency["sourceControl"]?[0];
                var registry = dependency["registry"]?[0];
                var descriptor = local ?? remote ?? registry;
                if (descriptor == null) continue;
                var identity = (string)descriptor["identity"];
                var location = (string)local?["path"] ?? (string)remote?["location"]?["remote"]?[0]?["urlString"] ?? (string)remote?["location"]?["local"]?[0] ?? identity;
                var pin = pins.FirstOrDefault(p => string.Equals((string)p["identity"] ?? (string)p["package"], identity, StringComparison.OrdinalIgnoreCase));
                var checkout = (state["object"]?["dependencies"] as JArray ?? new JArray()).FirstOrDefault(d => string.Equals((string)d["packageRef"]?["identity"], identity, StringComparison.OrdinalIgnoreCase));
                var directory = local != null ? location : checkout?["subpath"] == null ? null : Path.Combine(workspace, ".build", "checkouts", (string)checkout["subpath"]);
                if (directory != null && !File.Exists(Path.Combine(directory, "Package.swift"))) directory = null;
                result.Dependencies.Add(new SwiftDependency { Identity = identity, Source = location, Local = local != null, Directory = directory, Requirement = local != null ? "Local Path" : descriptor["requirement"]?.ToString(Newtonsoft.Json.Formatting.None), Version = (string)pin?["state"]?["version"] ?? (string)pin?["state"]?["branch"] ?? (string)pin?["state"]?["revision"] });
            }
            return result;
        }

        public static async Task<SwiftDependency> InspectAsync(string workspace, SwiftPackageRequest request, CancellationToken token, IProgress<string> progress = null)
        {
            string minimum = null;
            if (request.Requirement == "Latest Stable")
            {
                progress?.Report("Finding latest stable version…");
                minimum = await LatestVersionAsync(request.Source, token).ConfigureAwait(false);
                request = new SwiftPackageRequest { Source = request.Source, Requirement = "From Version", Value = minimum };
            }
            var toolchain = await Task.Run(() => Toolchain.Resolve(workspace), token).ConfigureAwait(false);
            var staging = Staging(workspace);
            File.WriteAllText(Path.Combine(staging, "Package.swift"), "// swift-tools-version: 6.0\nimport PackageDescription\nlet package = Package(name: \"VisualSwiftPackagePreview\")\n", new UTF8Encoding(false));
            try
            {
                await RunAsync(toolchain, staging, AddArguments(request, workspace), token).ConfigureAwait(false);
                progress?.Report("Downloading package for product discovery…");
                await RunAsync(toolchain, staging, "resolve", token, progress).ConfigureAwait(false);
                var graph = JObject.Parse(await RunAsync(toolchain, staging, "show-dependencies --format json", token).ConfigureAwait(false));
                var dependency = (graph["dependencies"] as JArray)?.FirstOrDefault() ?? throw new InvalidOperationException("SwiftPM did not resolve the supplied package.");
                var directory = (string)dependency["path"];
                var manifest = JObject.Parse(await RunAsync(toolchain, directory, "dump-package", token).ConfigureAwait(false));
                var products = (manifest["products"] as JArray ?? new JArray()).Where(p => p["type"]?["library"] != null).Select(p => (string)p["name"]).ToArray();
                if (products.Length == 0) throw new InvalidOperationException("This package does not provide a library product.");
                return new SwiftDependency { Identity = (string)dependency["identity"], PackageName = (string)manifest["name"], Products = products, Source = request.Source, Local = request.Local, Version = (string)dependency["version"], MinimumVersion = minimum };
            }
            finally { DeleteStaging(workspace, staging); }
        }

        public static async Task<SwiftPackageEdit> PreviewAddAsync(string workspace, SwiftPackageRequest request, string identity, CancellationToken token)
        {
            var toolchain = await Task.Run(() => Toolchain.Resolve(workspace), token).ConfigureAwait(false);
            var original = File.ReadAllText(Path.Combine(workspace, "Package.swift"));
            var staging = Staging(workspace);
            File.WriteAllText(Path.Combine(staging, "Package.swift"), original, new UTF8Encoding(false));
            try
            {
                await RunAsync(toolchain, staging, AddArguments(request, workspace), token).ConfigureAwait(false);
                await RunAsync(toolchain, staging, "add-target-dependency " + Quote(request.Product) + " " + Quote(request.Target) + " --package " + Quote(identity), token).ConfigureAwait(false);
                var updated = File.ReadAllText(Path.Combine(staging, "Package.swift"));
                return new SwiftPackageEdit { Workspace = workspace, Original = original, Updated = updated };
            }
            finally { DeleteStaging(workspace, staging); }
        }

        public static SwiftPackageEdit PreviewRemove(string workspace, SwiftDependency dependency)
        {
            var original = File.ReadAllText(Path.Combine(workspace, "Package.swift"));
            return new SwiftPackageEdit { Workspace = workspace, Original = original, Updated = SwiftManifestDependencies.Remove(original, workspace, dependency.Source, dependency.Identity, dependency.PackageName, dependency.Products) };
        }

        public static async Task LoadProductsAsync(string workspace, SwiftDependency dependency, CancellationToken token)
        {
            if (dependency.Directory == null) throw new InvalidOperationException("Resolve this package before removing it.");
            var toolchain = await Task.Run(() => Toolchain.Resolve(workspace), token).ConfigureAwait(false);
            var manifest = JObject.Parse(await RunAsync(toolchain, dependency.Directory, "dump-package", token).ConfigureAwait(false));
            dependency.PackageName = (string)manifest["name"];
            dependency.Products = (manifest["products"] as JArray ?? new JArray()).Select(product => (string)product["name"]).ToArray();
        }

        public static Task ApplyAsync(SwiftPackageEdit edit, CancellationToken token, IProgress<string> progress = null) => ExclusiveAsync(edit.Workspace, async () =>
        {
            var path = Path.Combine(edit.Workspace, "Package.swift");
            if (File.ReadAllText(path) != edit.Original) throw new InvalidOperationException("Package.swift changed after preview. Refresh and preview the edit again.");
            var resolvedPath = Path.Combine(edit.Workspace, "Package.resolved");
            var resolved = File.Exists(resolvedPath) ? File.ReadAllBytes(resolvedPath) : null;
            var originalBytes = File.ReadAllBytes(path);
            File.WriteAllText(path, edit.Updated, new UTF8Encoding(false));
            try
            {
                var toolchain = await Task.Run(() => Toolchain.Resolve(edit.Workspace), token).ConfigureAwait(false);
                await RunAsync(toolchain, edit.Workspace, "dump-package", token).ConfigureAwait(false);
                await RunAsync(toolchain, edit.Workspace, "resolve", token, progress).ConfigureAwait(false);
            }
            catch
            {
                File.WriteAllBytes(path, originalBytes);
                if (resolved != null) File.WriteAllBytes(resolvedPath, resolved);
                else if (File.Exists(resolvedPath)) File.Delete(resolvedPath);
                throw;
            }
        }, token);

        public static Task ResolveAsync(string workspace, CancellationToken token) => OperationAsync(workspace, "resolve", token);
        public static Task UpdateAsync(string workspace, string identity, CancellationToken token) => OperationAsync(workspace, "update" + (identity == null ? "" : " " + Quote(identity)), token);
        private static Task OperationAsync(string workspace, string operation, CancellationToken token) => ExclusiveAsync(workspace, async () =>
        {
            var toolchain = await Task.Run(() => Toolchain.Resolve(workspace), token).ConfigureAwait(false);
            await RunAsync(toolchain, workspace, operation, token).ConfigureAwait(false);
        }, token);

        private static async Task ExclusiveAsync(string workspace, Func<Task> action, CancellationToken token)
        {
            var gate = Gates.GetOrAdd(Path.GetFullPath(workspace), _ => new SemaphoreSlim(1));
            if (!await gate.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("A SwiftPM operation is already running for this project.");
            try { await action().ConfigureAwait(false); }
            finally { gate.Release(); }
        }

        private static string AddArguments(SwiftPackageRequest request, string workspace)
        {
            if (string.IsNullOrWhiteSpace(request.Source)) throw new InvalidOperationException("Enter a package URL or local path.");
            if (request.Local) return "add-dependency " + Quote(Path.GetFullPath(Path.IsPathRooted(request.Source) ? request.Source : Path.Combine(workspace, request.Source))) + " --type path";
            var option = request.Requirement == "Exact Version" ? "--exact" : request.Requirement == "Branch" ? "--branch" : request.Requirement == "Revision" ? "--revision" : request.Requirement == "Version Range" ? "--from" : "--from";
            if (string.IsNullOrWhiteSpace(request.Value)) throw new InvalidOperationException("Enter the required version, branch or revision.");
            if (request.Requirement == "Version Range")
            {
                var bounds = request.Value.Split(',');
                if (bounds.Length != 2) throw new InvalidOperationException("Enter the version range as lower,upper (upper bound is exclusive).");
                return "add-dependency " + Quote(request.Source) + " --from " + Quote(bounds[0].Trim()) + " --to " + Quote(bounds[1].Trim());
            }
            return "add-dependency " + Quote(request.Source) + " " + option + " " + Quote(request.Value.Trim());
        }

        private static async Task<string> RunAsync(Toolchain toolchain, string workspace, string operation, CancellationToken token, IProgress<string> progress = null)
        {
            PrototypeLog.Write("SwiftPM [" + workspace + "]: " + operation);
            var output = await SwiftProcess.RunAsync(toolchain, "package " + operation, workspace, token, TimeSpan.FromMinutes(10), line => { if (progress != null) progress.Report(line); if (operation == "resolve") PrototypeLog.Write(line); }).ConfigureAwait(false);
            if (operation == "resolve" || operation.StartsWith("update", StringComparison.Ordinal)) PrototypeLog.Write(output);
            return output;
        }
        internal static string LatestVersion(string tags)
        {
            var versions = tags.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => Regex.Match(line, @"refs/tags/v?(\d+)\.(\d+)\.(\d+)$"))
                .Where(match => match.Success)
                .Select(match => Version.Parse(match.Groups[1].Value + "." + match.Groups[2].Value + "." + match.Groups[3].Value))
                .OrderByDescending(version => version).ToArray();
            if (versions.Length == 0) throw new InvalidOperationException("This repository has no stable semantic version tags. Select Branch or Revision instead.");
            return versions[0].ToString(3);
        }
        internal static async Task<string> LatestVersionAsync(string source, CancellationToken token)
        {
            var info = new ProcessStartInfo("git", "ls-remote --tags --refs -- " + Quote(source)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            info.EnvironmentVariables["GCM_INTERACTIVE"] = "Never";
            using (var process = new Process { StartInfo = info, EnableRaisingEvents = true })
            using (var job = new SwiftProcessJob())
            {
                var exit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (_, __) => exit.TrySetResult(true);
                if (!process.Start()) throw new InvalidOperationException("Git could not be started.");
                job.Attach(process);
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                var completed = Task.WhenAll(exit.Task, output, error);
                if (await Task.WhenAny(completed, Task.Delay(TimeSpan.FromMinutes(1), token)).ConfigureAwait(false) != completed)
                {
                    job.Dispose();
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException("Git did not return version tags within 60 seconds.");
                }
                await completed.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) throw new InvalidOperationException("Could not read package versions: " + await error.ConfigureAwait(false));
                return LatestVersion(await output.ConfigureAwait(false));
            }
        }

        private static string Staging(string workspace)
        {
            var directory = Path.Combine(workspace, ".vs", "VisualSwift", "PackageEdits", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }
        private static void DeleteStaging(string workspace, string staging)
        {
            var root = Path.GetFullPath(Path.Combine(workspace, ".vs", "VisualSwift", "PackageEdits")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(staging).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid SwiftPM staging path.");
            var extended = staging.StartsWith(@"\\?\", StringComparison.Ordinal) ? staging : staging.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + staging.Substring(2) : @"\\?\" + Path.GetFullPath(staging);
            if (Directory.Exists(extended)) { ClearReadOnly(extended); Directory.Delete(extended, true); }
        }
        private static void ClearReadOnly(string directory)
        {
            foreach (var file in Directory.GetFiles(directory))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReparsePoint) == 0 && (attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
            foreach (var child in Directory.GetDirectories(directory))
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) ClearReadOnly(child);
        }
        internal static string Quote(string argument)
        {
            if (argument == null || argument.IndexOf('\0') >= 0 || argument.IndexOf('\r') >= 0 || argument.IndexOf('\n') >= 0) throw new ArgumentException("Invalid command argument.");
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }
    }
}
