using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VisualSwift
{
    internal sealed class Toolchain
    {
        private Toolchain(string binDirectory)
        {
            BinDirectory = binDirectory;
        }

        public string BinDirectory { get; }

        public static Toolchain Resolve(string workspace = null, SwiftSettings settings = null)
        {
            var configured = (settings ?? SwiftSettings.Default).Read(workspace).Path;
            if (!string.IsNullOrWhiteSpace(configured))
                return FromPath(configured);

            foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var candidate = entry.Trim().Trim('"');
                if (IsToolchain(candidate))
                    return new Toolchain(candidate);
            }

            throw new InvalidOperationException("Swift was not found. Install a Swift toolchain or set VISUALSWIFT_TOOLCHAIN before starting Visual Studio.");
        }

        public static Toolchain FromPath(string path)
        {
            var root = Path.GetFullPath(path.Trim().Trim('"'));
            var match = new[] { root, Path.Combine(root, "usr", "bin") }.FirstOrDefault(IsToolchain);
            if (match == null) throw new InvalidOperationException("Select a Swift toolchain containing swift.exe and sourcekit-lsp.exe in its bin directory.");
            return new Toolchain(match);
        }

        public async Task<string> ValidateAsync(CancellationToken token)
        {
            var version = await SwiftProcess.RunAsync(this, "--version", null, token).ConfigureAwait(false);
            var sdk = Environment.GetEnvironmentVariable("SDKROOT");
            if (string.IsNullOrWhiteSpace(sdk) || !Directory.Exists(sdk))
                throw new InvalidOperationException(version.Trim() + Environment.NewLine + "SDKROOT is missing or invalid. Configure the Windows Swift SDK environment and restart Visual Studio.");
            return version.Trim();
        }

        public ProcessStartInfo CreateSwiftStartInfo(string arguments, string workspace)
        {
            var info = CreateServerStartInfo(workspace);
            info.FileName = Path.Combine(BinDirectory, "swift.exe");
            info.Arguments = arguments;
            info.RedirectStandardInput = false;
            return info;
        }

        public ProcessStartInfo CreateServerStartInfo(string workspace)
        {
            var info = new ProcessStartInfo(Path.Combine(BinDirectory, "sourcekit-lsp.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workspace ?? BinDirectory
            };
            info.EnvironmentVariables["PATH"] = BinDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            return info;
        }

        private static bool IsToolchain(string directory)
        {
            return !string.IsNullOrWhiteSpace(directory)
                && File.Exists(Path.Combine(directory, "swift.exe"))
                && File.Exists(Path.Combine(directory, "sourcekit-lsp.exe"));
        }
    }
}
