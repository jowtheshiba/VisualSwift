using VisualSwift;
using Newtonsoft.Json;

Assert(SwiftPackages.LatestVersion("abc refs/tags/1.2.0\nabc refs/tags/v2.0.0\nabc refs/tags/9.0.0-beta.1\nabc refs/tags/2.0.0^{}") == "2.0.0", "Latest stable tag selection");
if (args.Length > 2) Console.WriteLine("Latest remote version: " + await SwiftPackages.LatestVersionAsync(args[2], CancellationToken.None));
var root = Path.GetFullPath(args[1]);
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("VISUALSWIFT_TOOLCHAIN", args[0]);
var lib = Path.Combine(root, "Library with spaces");
Directory.CreateDirectory(Path.Combine(lib, "Sources", "Greeting"));
File.WriteAllText(Path.Combine(lib, "Package.swift"), "// swift-tools-version: 6.0\nimport PackageDescription\nlet package = Package(name: \"LocalLibrary\", products: [.library(name: \"Greeting\", targets: [\"Greeting\"])], targets: [.target(name: \"Greeting\")])\n");
File.WriteAllText(Path.Combine(lib, "Sources", "Greeting", "Greeter.swift"), "public struct Greeter { public init() {} }\n");
var app = Path.Combine(root, "App");
var other = Path.Combine(root, "Other");
var manifest = "// swift-tools-version: 6.0\nimport PackageDescription\nlet package = Package(name: \"App\", targets: [.executableTarget(name: \"App\")])\n";
foreach (var path in new[] { app, other }) {
    Directory.CreateDirectory(Path.Combine(path, "Sources", "App"));
    File.WriteAllText(Path.Combine(path, "Package.swift"), manifest);
    File.WriteAllText(Path.Combine(path, "Sources", "App", "main.swift"), "print(\"Hello\")\n");
}
if (args.Length > 2) { var remote = await SwiftPackages.InspectAsync(app, new SwiftPackageRequest { Source = args[2], Requirement = "Latest Stable" }, CancellationToken.None, new Progress<string>(Console.WriteLine)); Assert(remote.MinimumVersion == remote.Version, "Latest version resolved"); Console.WriteLine("Remote products: " + string.Join(", ", remote.Products)); var remoteEdit = await SwiftPackages.PreviewAddAsync(other, new SwiftPackageRequest { Source = args[2], Requirement = "From Version", Value = remote.MinimumVersion, Product = remote.Products[0], Target = "App" }, remote.Identity, CancellationToken.None); await SwiftPackages.ApplyAsync(remoteEdit, CancellationToken.None); var remoteState = await SwiftPackages.ReadAsync(other, CancellationToken.None); Assert(remoteState.Dependencies[0].Version == remote.Version && Directory.Exists(remoteState.Dependencies[0].Directory), "Remote checkout and version"); Console.WriteLine("Remote checkout: " + remoteState.Dependencies[0].Directory); await SwiftPackages.LoadProductsAsync(other, remoteState.Dependencies[0], CancellationToken.None); await SwiftPackages.ApplyAsync(SwiftPackages.PreviewRemove(other, remoteState.Dependencies[0]), CancellationToken.None); File.WriteAllText(Path.Combine(other, "Package.swift"), manifest); }
var profileRoot = Path.Combine(root, "ProfileSmoke");
Directory.CreateDirectory(Path.Combine(profileRoot, "Sources", "App"));
Directory.CreateDirectory(Path.Combine(profileRoot, "RunFolder"));
Directory.CreateDirectory(Path.Combine(profileRoot, "VisualSwift"));
File.WriteAllText(Path.Combine(profileRoot, "Package.swift"), "// swift-tools-version: 6.0\nimport PackageDescription\nlet package = Package(name: \"ProfileSmoke\", products: [.executable(name: \"ProfileSmoke\", targets: [\"App\"])], targets: [.executableTarget(name: \"App\")])\n");
File.WriteAllText(Path.Combine(profileRoot, "Sources", "App", "main.swift"), "import Foundation\n#if DEBUG_SAMPLE\nprint(\"debug profile\")\n#elseif RELEASE_SAMPLE\nprint(\"release profile\")\n#else\n#error(\"Missing profile condition\")\n#endif\nprint(CommandLine.arguments.dropFirst().joined(separator: \"|\"))\nprint(ProcessInfo.processInfo.environment[\"PROFILE_MARK\"] ?? \"missing\")\nprint(FileManager.default.currentDirectoryPath)\n");
var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
foreach (var helper in new[] { "Invoke-Swift.ps1", "WindowsSwiftEnvironment.ps1" }) File.Copy(Path.Combine(repository, "templates", "Shared", helper), Path.Combine(profileRoot, "VisualSwift", helper), true);
SwiftSettings.Default.Save(args[0], profileRoot, true);
SwiftSettings.Default.SaveInlayHints(new InlayHintSettings(false, true, false), profileRoot, true);
var profiles = new SwiftProjectSettings {
    Debug = new SwiftProjectProfile { Product = "ProfileSmoke", Conditions = ["DEBUG_SAMPLE"], Arguments = ["hello world", "second"], Environment = new Dictionary<string, string> { ["PROFILE_MARK"] = "debug=ok" }, WorkingDirectory = "RunFolder", BuildBeforeLaunch = false, UseF5 = true },
    Release = new SwiftProjectProfile { Product = "ProfileSmoke", Conditions = ["RELEASE_SAMPLE"], Optimization = "-Osize", Arguments = ["release arg"], Environment = new Dictionary<string, string> { ["PROFILE_MARK"] = "release=ok" }, UseF5 = true }
};
profiles.Save(profileRoot);
Assert(SwiftSettings.Default.Read(profileRoot).Path == args[0] && !SwiftSettings.Default.ReadInlayHints(profileRoot).Enabled, "Profiles preserve toolchain and hints");
var restoredProfiles = SwiftProjectSettings.Read(profileRoot);
Assert(restoredProfiles.Profile("Release").Conditions.SequenceEqual(new[] { "RELEASE_SAMPLE" }), "Release profile persistence");
Assert(SwiftDebugOptions.Read(profileRoot, "Release").Arguments.SequenceEqual(new[] { "release arg" }), "F5 uses Release profile");
foreach (var configuration in new[] { "Debug", "Release" }) {
    await RunHelperAsync(profileRoot, "Build", configuration);
    var run = await RunHelperAsync(profileRoot, "Run", configuration);
    Assert(run.Contains(configuration.ToLowerInvariant() + " profile") && run.Contains(configuration.ToLowerInvariant() + "=ok"), "Configuration conditions and environment");
    Assert(run.Contains(configuration == "Debug" ? "hello world|second" : "release arg"), "Launch arguments with spaces");
    if (configuration == "Debug") Assert(run.Contains(Path.Combine(profileRoot, "RunFolder")), "Run working directory");
}
try { new SwiftProjectProfile { Conditions = ["bad condition"] }.Validate(); throw new Exception("Invalid condition accepted"); } catch (InvalidOperationException) { }
Console.WriteLine("Project profiles passed: Debug/Release build conditions, optimization, launch arguments, environment, working directory, skip-build, persistence, F5 profile selection and settings preservation.");
var request = new SwiftPackageRequest { Source = lib, Requirement = "Local Path", Product = "Greeting", Target = "App" };
var dependency = await SwiftPackages.InspectAsync(app, request, CancellationToken.None);
Assert(dependency.Products.SequenceEqual(new[] { "Greeting" }), "Library products");
Assert(!string.IsNullOrEmpty(dependency.Identity), "Package identity");
var edit = await SwiftPackages.PreviewAddAsync(app, request, dependency.Identity, CancellationToken.None);
Assert(File.ReadAllText(Path.Combine(app, "Package.swift")) == manifest, "Preview is read-only");
await SwiftPackages.ApplyAsync(edit, CancellationToken.None);
var snapshot = await SwiftPackages.ReadAsync(app, CancellationToken.None);
Assert(snapshot.Dependencies.Count == 1 && snapshot.Dependencies[0].Directory == lib, "Local dependency node");
Assert(File.ReadAllText(Path.Combine(other, "Package.swift")) == manifest, "Project isolation");
File.WriteAllText(Path.Combine(app, "Sources", "App", "main.swift"), "import Greeting\nlet greeter = Greeter()\nprint(greeter)\n");
await SwiftProcess.RunAsync(Toolchain.Resolve(app), "build", app, CancellationToken.None, TimeSpan.FromMinutes(3));
await SwiftPackages.ResolveAsync(app, CancellationToken.None);
await SwiftPackages.UpdateAsync(app, null, CancellationToken.None);
var selected = snapshot.Dependencies[0];
await SwiftPackages.LoadProductsAsync(app, selected, CancellationToken.None);
var removal = SwiftPackages.PreviewRemove(app, selected);
Assert(!removal.Updated.Contains(".package(") && !removal.Updated.Contains(".product("), "Remove package and product");
await SwiftPackages.ApplyAsync(removal, CancellationToken.None);
Assert((await SwiftPackages.ReadAsync(app, CancellationToken.None)).Dependencies.Count == 0, "Remove resolves");
var current = File.ReadAllText(Path.Combine(app, "Package.swift"));
try { await SwiftPackages.ApplyAsync(new SwiftPackageEdit { Workspace = app, Original = current, Updated = "invalid swift" }, CancellationToken.None); throw new Exception("Invalid manifest accepted"); } catch (InvalidOperationException) { }
Assert(File.ReadAllText(Path.Combine(app, "Package.swift")) == current, "Failed apply rolls back");
try { await SwiftPackages.ApplyAsync(edit, CancellationToken.None); throw new Exception("Stale edit accepted"); } catch (InvalidOperationException) { }
var expression = "import PackageDescription\nlet package = Package(name: \"App\", dependencies: [.package(url: \"https://host/lib\" + suffix, from: \"1.0.0\")])";
try { SwiftManifestDependencies.Remove(expression, app, "https://host/lib", "lib", "Lib", []); throw new Exception("Computed URL accepted"); } catch (InvalidOperationException) { }
var relative = "import PackageDescription\nlet package = Package(name: \"App\", dependencies: [.package(path: \"../Library with spaces\")], targets: [.executableTarget(name: \"App\", dependencies: [.product(name: \"Greeting\", package: \"LocalLibrary\")])])";
Assert(!SwiftManifestDependencies.Remove(relative, app, lib, dependency.Identity, "LocalLibrary", ["Greeting"]).Contains(".product("), "Relative path removal");
Console.WriteLine(JsonConvert.SerializeObject(new { Passed = true, dependency.Identity, dependency.Products, Checks = new[] { "inspect", "preview", "apply", "build product", "resolve", "update all", "remove", "rollback", "stale preview", "computed expression refusal", "relative paths", "project isolation" } }, Formatting.Indented));
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

static async Task<string> RunHelperAsync(string root, string operation, string configuration) {
    var info = new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File " + SwiftPackages.Quote(Path.Combine(root, "VisualSwift", "Invoke-Swift.ps1")) + " -Command " + operation + " -Configuration " + configuration) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    using var process = System.Diagnostics.Process.Start(info);
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    await Task.Run(() => process.WaitForExit());
    var result = await stdout;
    if (process.ExitCode != 0) throw new Exception(result + await stderr);
    return result;
}