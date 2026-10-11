using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace VisualSwift
{
    internal sealed class Toolchain
    {
        private Toolchain(string binDirectory)
        {
            BinDirectory = binDirectory;
        }

        public string BinDirectory { get; }

        public static Toolchain Resolve()
        {
            var configured = Environment.GetEnvironmentVariable("VISUALSWIFT_TOOLCHAIN");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var root = Path.GetFullPath(configured);
                var candidates = new[] { root, Path.Combine(root, "usr", "bin") };
                var match = candidates.FirstOrDefault(IsToolchain);
                if (match == null)
                    throw new InvalidOperationException("The configured Swift toolchain must contain swift.exe and sourcekit-lsp.exe in its bin directory.");
                return new Toolchain(match);
            }

            foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var candidate = entry.Trim().Trim('"');
                if (IsToolchain(candidate))
                    return new Toolchain(candidate);
            }

            throw new InvalidOperationException("Swift was not found. Install a Swift toolchain or set VISUALSWIFT_TOOLCHAIN before starting Visual Studio.");
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
