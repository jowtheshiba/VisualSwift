using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace VisualSwift
{
    internal static class SwiftWindowsEnvironment
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<string, Dictionary<string, string>> environments = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public static void Apply(ProcessStartInfo info, string bin)
        {
            if (!File.Exists(Path.Combine(bin, "swiftc.exe"))) return;
            Dictionary<string, string> environment;
            lock (gate)
            {
                if (!environments.TryGetValue(bin, out environment))
                {
                    environment = Load(bin);
                    environments[bin] = environment;
                }
            }
            foreach (var entry in environment) info.EnvironmentVariables[entry.Key] = entry.Value;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD002", Justification = "The bounded child-process stream tasks have no UI synchronization context; process startup requires a synchronous environment snapshot.")]
        private static Dictionary<string, string> Load(string bin)
        {
            var script = Path.Combine(Path.GetDirectoryName(typeof(SwiftWindowsEnvironment).Assembly.Location), "Runtime", "WindowsSwiftEnvironment.ps1");
            if (!File.Exists(script)) throw new InvalidOperationException("The Swift Windows build environment helper is missing. Reinstall VisualSwift.");
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -EnvironmentToolchainBin \"" + bin.TrimEnd('\\') + "\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = new Process { StartInfo = info })
            using (var job = new SwiftProcessJob())
            {
                if (!process.Start()) throw new InvalidOperationException("The Swift Windows build environment could not be initialized.");
                job.Attach(process);
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(45000)) { job.Dispose(); throw new TimeoutException("The Swift Windows build environment initialization timed out."); }
                Task.WhenAll(output, errors).GetAwaiter().GetResult();
                if (process.ExitCode != 0) throw new InvalidOperationException(errors.GetAwaiter().GetResult().Trim());
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(output.GetAwaiter().GetResult()) ?? throw new InvalidOperationException("The Swift Windows build environment is empty.");
            }
        }
    }
}
