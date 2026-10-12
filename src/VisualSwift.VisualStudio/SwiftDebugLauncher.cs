using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
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
        public static SwiftDebugOptions Read(string workspace)
        {
            if (workspace == null || !File.Exists(FilePath(workspace))) return new SwiftDebugOptions();
            var options = JObject.Parse(File.ReadAllText(FilePath(workspace))).ToObject<SwiftDebugOptions>();
            if (options.Arguments == null) throw new InvalidOperationException("Swift debug arguments must be an array of strings.");
            return options;
        }
        public void Save(string workspace)
        {
            if (workspace == null) throw new InvalidOperationException("Open a Swift package first.");
            var path = FilePath(workspace);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JObject.FromObject(this).ToString());
        }
    }

    internal static class SwiftDebugLauncher
    {
        public const string Engine = "37c5e2ce-7a3a-42a8-9dac-f6d7f284ae6c";
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1);
        public static bool IsPreparing { get; private set; }
        public static async Task StartAsync(string workspace, SwiftDebugOptions options, CancellationToken token)
        {
            if (!await Gate.WaitAsync(0, token)) throw new InvalidOperationException("A Swift debug launch is already being prepared.");
            IsPreparing = true;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
                if (dte?.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode) throw new InvalidOperationException("Stop the current debug session before starting Swift debugging.");
                var toolchain = await Task.Run(() => Toolchain.Resolve(workspace), token);
                var adapter = Path.Combine(toolchain.BinDirectory, "lldb-dap.exe");
                if (!File.Exists(adapter) || !File.Exists(Path.Combine(toolchain.BinDirectory, "lld-link.exe"))) throw new InvalidOperationException("The selected Swift toolchain must include lldb-dap.exe and lld-link.exe.");
                await toolchain.ValidateAsync(token);
                var description = JObject.Parse(await SwiftProcess.RunAsync(toolchain, "package describe --type json", workspace, token));
                var products = (description["products"] as JArray ?? new JArray()).Where(p => p["type"]?["executable"] != null).Select(p => p.Value<string>("name")).ToArray();
                if (!products.Contains(options.Product)) throw new InvalidOperationException("Select an executable SwiftPM product on the Debug page.");
                PrototypeLog.Write("Building Swift debug product " + options.Product + " with LLVM/DWARF.");
                var build = await SwiftProcess.RunAsync(toolchain, "build -c debug --product " + Quote(options.Product) + " -Xswiftc -use-ld=lld -Xlinker /debug:dwarf", workspace, token, TimeSpan.FromMinutes(5));
                PrototypeLog.Write(build);
                var bin = (await SwiftProcess.RunAsync(toolchain, "build -c debug --show-bin-path", workspace, token)).Trim();
                var executable = Path.Combine(bin, options.Product + ".exe");
                if (!File.Exists(executable)) throw new InvalidOperationException("SwiftPM did not produce the selected executable: " + executable);
                var launch = new JObject
                {
                    ["$adapter"] = adapter, ["$localsScopeName"] = "Locals", ["type"] = "swift", ["request"] = "launch",
                    ["name"] = "Swift: " + options.Product, ["program"] = executable, ["cwd"] = workspace,
                    ["args"] = new JArray(options.Arguments), ["stopOnEntry"] = options.StopOnEntry,
                    ["env"] = new JObject { ["PATH"] = toolchain.BinDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") }
                };
                var path = Path.Combine(workspace, ".vs", "VisualSwift", "launch.json");
                await Task.Run(() => { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, launch.ToString()); }, token);
                token.ThrowIfCancellationRequested();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode) throw new InvalidOperationException("Another debug session started while building. Stop it and retry.");
                PrototypeLog.Write("Launching Swift debugger: " + executable);
                dte.Commands.Raise("0ddba113-7ac1-4c6e-a2ef-dcac3f9e731e", 0x0101, "/LaunchJson:" + Quote(path) + " /EngineGuid:" + Quote(Engine), IntPtr.Zero);
            }
            finally { IsPreparing = false; Gate.Release(); }
        }
        private static string Quote(string argument)
        {
            var result = new System.Text.StringBuilder("\"");
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
