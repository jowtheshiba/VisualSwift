using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace VisualSwift
{
    internal static class SwiftRuntime
    {
        private static readonly object gate = new object();
        private static string workspace, state = "Stopped", activeBin, activeWorkspace, error, selectedVersion;
        private static string[] workspaces = Array.Empty<string>();
        private static readonly Dictionary<string, ServerState> servers = new Dictionary<string, ServerState>(StringComparer.OrdinalIgnoreCase);
        private sealed class ServerState { internal string State, Bin, Error; }
        public static event Action Changed;
        public static event Action ProjectsChanged;
        public static Func<Task> Restart { get; set; }
        public static Func<string, object, Task<Newtonsoft.Json.Linq.JToken>> Request { get; set; }
        public static string Workspace { get { lock (gate) return workspace; } }
        public static string[] Workspaces { get { lock (gate) return (string[])workspaces.Clone(); } }
        public static string State { get { lock (gate) return workspace != null && servers.TryGetValue(workspace, out var server) ? server.State : state; } }
        public static string ActiveBin { get { lock (gate) return workspace != null && servers.TryGetValue(workspace, out var server) ? server.Bin : activeBin; } }
        public static string ActiveWorkspace { get { lock (gate) return workspace != null && servers.ContainsKey(workspace) ? workspace : activeWorkspace; } }
        public static string Error { get { lock (gate) return workspace != null && servers.TryGetValue(workspace, out var server) ? server.Error : error; } }
        public static string SelectedVersion { get { lock (gate) return selectedVersion; } }

        public static void SetWorkspace(string path)
        {
            lock (gate) { if (workspace == path) return; workspace = path; selectedVersion = null; }
            Notify();
        }
        public static void SetServer(string status, string bin = null, string package = null, string failure = null)
        {
            lock (gate)
            {
                if (package != null) servers[package] = new ServerState { State = status, Bin = bin, Error = failure };
                else { state = status; activeBin = bin; activeWorkspace = package; error = failure; if (status == "Stopped") servers.Clear(); }
            }
            Notify();
        }
        public static void SetProjects(IEnumerable<string> paths)
        {
            var next = paths.Where(path => path != null).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            lock (gate) { if (workspaces.SequenceEqual(next, StringComparer.OrdinalIgnoreCase)) return; workspaces = next; }
            ProjectsChanged?.Invoke(); Notify();
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
            var document = string.IsNullOrWhiteSpace(dte?.ActiveDocument?.FullName) ? null : ForUri(new Uri(dte.ActiveDocument.FullName).AbsoluteUri);
            if (document != null) return document;
            try
            {
                if (dte?.SelectedItems.Count == 1)
                {
                    var item = dte.SelectedItems.Item(1);
                    var selected = FindPackage((item.ProjectItem?.ContainingProject ?? item.Project)?.FullName);
                    if (selected != null) return selected;
                }
            }
            catch (System.Runtime.InteropServices.COMException) { }
            var projects = Projects(dte);
            if (projects.Length == 1) return projects[0];
            if (projects.Contains(SwiftRuntime.Workspace, StringComparer.OrdinalIgnoreCase)) return SwiftRuntime.Workspace;
            if (projects.Length > 0) return projects[0];
            return FindPackage(dte?.Solution?.FileName);
        }

        internal static string[] Projects(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var roots = new List<string>();
            if (dte?.Solution.IsOpen == true) foreach (Project project in dte.Solution.Projects) Collect(project, roots);
            return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        private static void Collect(Project project, List<string> roots)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (project.Kind == Constants.vsProjectKindSolutionItems)
                {
                    foreach (ProjectItem item in project.ProjectItems) if (item.SubProject != null) Collect(item.SubProject, roots);
                }
                else
                {
                    var root = FindPackage(project.FullName);
                    if (root != null) roots.Add(root);
                }
            }
            catch (System.Runtime.InteropServices.COMException) { }
        }

        internal static string ForUri(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return null;
            var file = Path.GetFullPath(parsed.LocalPath);
            return SwiftRuntime.Workspaces.OrderByDescending(root => root.Length).FirstOrDefault(root => file.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ?? FindPackage(file);
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
