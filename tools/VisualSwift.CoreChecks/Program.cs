using System.Diagnostics;
using VisualSwift;

if (args.Length != 2) throw new ArgumentException("Usage: VisualSwift.CoreChecks <swift-bin> <fixture-directory>");
var root = Path.GetFullPath(args[1]);
Directory.CreateDirectory(root);
var workspace = Path.Combine(root, "package with spaces");
Directory.CreateDirectory(workspace);
var globalPath = Path.Combine(root, "global", "settings.json");
var settings = new SwiftSettings(globalPath);
var swift = Toolchain.FromPath(args[0]);
var originalEnvironment = Environment.GetEnvironmentVariable("VISUALSWIFT_TOOLCHAIN");
var originalLibraries = Environment.GetEnvironmentVariable("LIB");
try
{
    Environment.SetEnvironmentVariable("VISUALSWIFT_TOOLCHAIN", swift.BinDirectory);
    Assert(settings.Read(workspace).Scope == "Environment", "Environment fallback failed.");
    settings.Save(swift.BinDirectory, null, false);
    Assert(new SwiftSettings(globalPath).Read(workspace).Scope == "Global", "Global selection did not survive reload.");
    var fakeBin = Path.Combine(root, "fake bin");
    Directory.CreateDirectory(fakeBin);
    File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(fakeBin, "swift.exe"));
    File.WriteAllText(Path.Combine(fakeBin, "sourcekit-lsp.exe"), "fixture");
    settings.Save(fakeBin, workspace, true);
    Assert(new SwiftSettings(globalPath).Read(workspace).Path == fakeBin, "Workspace override did not survive reload.");
    Assert(Toolchain.Resolve(workspace, settings).BinDirectory == fakeBin, "Workspace override lost priority.");
    settings.ClearWorkspace(workspace);
    Assert(Toolchain.Resolve(workspace, new SwiftSettings(globalPath)).BinDirectory == swift.BinDirectory, "Clearing workspace override did not restore global selection.");
    settings.Save(Path.Combine(root, "missing"), workspace, true);
    try { Toolchain.Resolve(workspace, settings); throw new Exception("Invalid override silently fell back."); }
    catch (InvalidOperationException) { }
    settings.ClearWorkspace(workspace);
    Environment.SetEnvironmentVariable("LIB", @"C:\invalid\MSVC\lib\x64");
    var startInfo = swift.CreateSwiftStartInfo("--version", root);
    Assert(!startInfo.EnvironmentVariables["LIB"].Contains(@"C:\invalid"), "Swift inherited mismatched MSVC libraries.");
    Assert(startInfo.EnvironmentVariables["VSCMD_ARG_TGT_ARCH"] is "arm64" or "x64", "Swift target architecture was not initialized.");
    Assert(Environment.GetEnvironmentVariable("LIB") == @"C:\invalid\MSVC\lib\x64", "Compiler initialization modified the parent process environment.");
    var version = await swift.ValidateAsync(CancellationToken.None);
    Assert(settings.ReadInlayHints(workspace).Enabled && settings.ReadInlayHints(workspace).Types && settings.ReadInlayHints(workspace).Parameters, "Inlay hints defaults failed.");
    settings.SaveInlayHints(new InlayHintSettings(true, true, false), null, false);
    settings.SaveInlayHints(new InlayHintSettings(false, false, true), workspace, true);
    Assert(!new SwiftSettings(globalPath).ReadInlayHints(workspace).Enabled, "Workspace inlay hints override did not persist.");
    Assert(Toolchain.Resolve(workspace, settings).BinDirectory == swift.BinDirectory, "Saving hints changed the selected toolchain.");
    settings.Save(fakeBin, workspace, true);
    Assert(!settings.ReadInlayHints(workspace).Enabled, "Saving toolchain removed inlay hints.");
    settings.ClearWorkspace(workspace);
    Assert(!settings.ReadInlayHints(workspace).Enabled, "Clearing toolchain removed inlay hints.");
    settings.ClearWorkspaceInlayHints(workspace);
    var hints = new SwiftSettings(globalPath).ReadInlayHints(workspace);
    Assert(hints.Enabled && hints.Types && !hints.Parameters && hints.Scope == "Global", "Clearing workspace hints did not restore global settings.");
    var hintItems = Newtonsoft.Json.Linq.JArray.Parse("[{\"kind\":1},{\"kind\":2},{}]");
    Assert(hints.Filter(hintItems).Count == 2 && new InlayHintSettings(false, true, true).Filter(hintItems).Count == 0, "Inlay hint filtering failed.");
    Assert(version.Contains("Swift version"), "Compiler validation returned no version.");
    var fake = Toolchain.FromPath(fakeBin);
    Assert((await SwiftProcess.RunAsync(fake, "/c echo core-check", root, CancellationToken.None)).Contains("core-check"), "Process output was lost.");
    try { await SwiftProcess.RunAsync(fake, "/c exit /b 17", root, CancellationToken.None); throw new Exception("Nonzero exit was ignored."); }
    catch (InvalidOperationException error) { Assert(error.Message.Contains("17"), "Exit code was lost."); }
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
    var elapsed = Stopwatch.StartNew();
    try { await SwiftProcess.RunAsync(fake, "/c ping -n 30 127.0.0.1", root, cancellation.Token); throw new Exception("Cancellation was ignored."); }
    catch (OperationCanceledException) { Assert(elapsed.Elapsed < TimeSpan.FromSeconds(5), "Cancellation did not stop promptly."); }
    Console.WriteLine("Core checks passed: toolchain priority/persistence, inlay hints scope/filtering, invalid overrides, compiler validation, process output/exit and cancellation.");
}
finally { Environment.SetEnvironmentVariable("VISUALSWIFT_TOOLCHAIN", originalEnvironment); Environment.SetEnvironmentVariable("LIB", originalLibraries); }

static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
