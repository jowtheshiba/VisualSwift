using System;
using System.IO;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace VisualSwift
{
    internal static class SwiftRuntime
    {
        private static readonly object gate = new object();
        private static string workspace, state = "Stopped", activeBin, activeWorkspace, error, selectedVersion;
        public static event Action Changed;
        public static Func<Task> Restart { get; set; }
        public static string Workspace { get { lock (gate) return workspace; } }
        public static string State { get { lock (gate) return state; } }
        public static string ActiveBin { get { lock (gate) return activeBin; } }
        public static string ActiveWorkspace { get { lock (gate) return activeWorkspace; } }
        public static string Error { get { lock (gate) return error; } }
        public static string SelectedVersion { get { lock (gate) return selectedVersion; } }

        public static void SetWorkspace(string path)
        {
            lock (gate) { if (workspace == path) return; workspace = path; selectedVersion = null; }
            Notify();
        }
        public static void SetServer(string status, string bin = null, string package = null, string failure = null)
        {
            lock (gate) { state = status; activeBin = bin; activeWorkspace = package; error = failure; }
            Notify();
        }
        public static void SetVersion(string version) { lock (gate) selectedVersion = version; Notify(); }
        public static void Notify() => Changed?.Invoke();
    }

    internal static class SwiftWorkspace
    {
        public static string Current()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var configured = Environment.GetEnvironmentVariable("VISUALSWIFT_WORKSPACE");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var root = Path.GetFullPath(configured);
                if (!File.Exists(Path.Combine(root, "Package.swift"))) throw new InvalidOperationException("VISUALSWIFT_WORKSPACE must contain Package.swift.");
                return root;
            }
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            var document = FindPackage(dte?.ActiveDocument?.FullName);
            if (document != null) return document;
            return FindPackage(dte?.Solution?.FileName);
        }

        public static string FindPackage(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;
            var directory = Directory.Exists(file) ? new DirectoryInfo(file) : new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(file)));
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Package.swift"))) return directory.FullName;
                directory = directory.Parent;
            }
            return null;
        }
    }
}
