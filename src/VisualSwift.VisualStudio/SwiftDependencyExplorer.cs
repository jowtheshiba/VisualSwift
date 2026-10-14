using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.VCProjectEngine;

namespace VisualSwift
{
    internal sealed class SwiftDependencyExplorer : IDisposable
    {
        private sealed class ProjectState
        {
            public Project Project;
            public string Workspace;
            public SwiftPackageSnapshot Snapshot;
            public HashSet<string> Owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public SemaphoreSlim RefreshGate = new SemaphoreSlim(1);
            public FileSystemWatcher Watcher;
            public CancellationTokenSource Debounce;
            public string OwnershipPath;
        }
        private readonly DTE dte;
        private readonly CancellationToken token;
        private readonly Dictionary<string, ProjectState> projects = new Dictionary<string, ProjectState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SwiftPackagesWindow> windows = new Dictionary<string, SwiftPackagesWindow>(StringComparer.OrdinalIgnoreCase);
        private readonly SolutionEvents solutionEvents;
        private readonly DocumentEvents documentEvents;
        private bool disposed;
        public SwiftDependencyExplorer(DTE dte, OleMenuCommandService menu, CancellationToken token)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.dte = dte;
            this.token = token;
            solutionEvents = dte.Events.SolutionEvents;
            documentEvents = dte.Events.DocumentEvents;
            solutionEvents.Opened += Schedule;
            solutionEvents.ProjectAdded += ProjectAdded;
            solutionEvents.AfterClosing += Clear;
            documentEvents.DocumentSaved += DocumentSaved;
            var actions = new[] { "manage", "add", "resolve", "updateAll", "update", "remove", "open", "settings" };
            for (var index = 0; index < actions.Length; index++) Register(menu, 0x0201 + index, actions[index]);
            Schedule();
        }
        private void Register(OleMenuCommandService menu, int id, string action)
        {
            var command = new OleMenuCommand((_, __) => Start(action), new CommandID(new Guid("5d796222-e1ce-44f3-9492-5475d8dac383"), id));
            command.BeforeQueryStatus += (_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var selected = Selected(out var identity);
                command.Visible = selected != null && ((action != "update" && action != "remove" && action != "open") || identity != null);
                command.Enabled = command.Visible && !SwiftPackages.Busy(selected.Workspace);
            };
            menu.AddCommand(command);
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSSDK007", Justification = "The command task catches errors and is tracked by FileAndForget.")]
        private void Start(string action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var selected = Selected(out var identity);
            if (selected == null) return;
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    if (action == "settings") { new SwiftProjectSettingsWindow(selected.Project, selected.Workspace).ShowDialog(); return; }
                    if (selected.Snapshot == null) await RefreshAsync(selected);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                    if (action == "open")
                    {
                        var dependency = selected.Snapshot.Dependencies.FirstOrDefault(d => d.Identity == identity);
                        if (dependency?.Directory == null) throw new InvalidOperationException("Resolve this package before opening its manifest.");
                        VsShellUtilities.OpenDocument(SwiftPackage.Instance, Path.Combine(dependency.Directory, "Package.swift"));
                        return;
                    }
                    if (windows.TryGetValue(selected.Workspace, out var existing)) { existing.Activate(); await existing.InitializeAsync(action, identity); return; }
                    var window = new SwiftPackagesWindow(selected.Project.Name, selected.Workspace, () => RefreshAsync(selected));
                    windows.Add(selected.Workspace, window);
                    window.Closed += (_, __) => windows.Remove(selected.Workspace);
                    window.Show();
                    await window.InitializeAsync(action, identity);
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { PrototypeLog.Write("Swift dependency command failed: " + error); await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); System.Windows.MessageBox.Show(error.Message, "Swift Packages"); }
            }).FileAndForget("VisualSwift/DependencyCommand");
        }
        private ProjectState Selected(out string identity)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            identity = null;
            try
            {
                if (dte.SelectedItems.Count != 1) return null;
                var selection = dte.SelectedItems.Item(1);
                var item = selection.ProjectItem;
                var project = item?.ContainingProject ?? selection.Project;
                if (project == null || !project.FullName.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase)) return null;
                var workspace = SwiftWorkspace.FindPackage(project.FullName);
                if (workspace == null) return null;
                if (!projects.TryGetValue(project.FullName, out var state))
                {
                    state = CreateState(project, workspace);
                    projects.Add(project.FullName, state);
                }
                for (var node = item; node != null; node = node.Collection?.Parent as ProjectItem)
                {
                    var nodeName = node.Name;
                    if (state.Snapshot?.Dependencies.Any(d => d.Identity == nodeName) == true) identity = nodeName;
                    if (node.Name == "Dependencies") break;
                    if (node.Collection?.Parent is Project) identity = null;
                }
                return state;
            }
            catch (System.Runtime.InteropServices.COMException) { return null; }
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSSDK007", Justification = "The refresh task catches failures and is tracked by FileAndForget.")]
        private void Schedule()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed) return;
            var selectedProjects = new List<Project>();
            foreach (Project project in dte.Solution.Projects) Collect(project, selectedProjects);
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                foreach (var project in selectedProjects)
                {
                    try
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                        var path = project.FullName;
                        var workspace = SwiftWorkspace.FindPackage(path);
                        if (!path.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase) || workspace == null) continue;
                        if (!projects.TryGetValue(path, out var state)) projects[path] = state = CreateState(project, workspace);
                        await RefreshAsync(state);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception error) { PrototypeLog.Write("Swift dependency discovery: " + error.Message); }
                }
            }).FileAndForget("VisualSwift/DependencyDiscovery");
        }
        private static void Collect(Project project, List<Project> result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (project.Kind != Constants.vsProjectKindSolutionItems) { result.Add(project); return; }
            foreach (ProjectItem item in project.ProjectItems) if (item.SubProject != null) Collect(item.SubProject, result);
        }
        private void ProjectAdded(Project project) { ThreadHelper.ThrowIfNotOnUIThread(); Schedule(); }
        private void DocumentSaved(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Path.GetFileName(document.FullName) == "Package.swift") Schedule();
        }
        private async Task RefreshAsync(ProjectState state)
        {
            await state.RefreshGate.WaitAsync(token);
            try { await RefreshCoreAsync(state); }
            finally { state.RefreshGate.Release(); }
        }
        private async Task RefreshCoreAsync(ProjectState state)
        {
            var snapshot = await SwiftPackages.ReadAsync(state.Workspace, token);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            if (disposed || !dte.Solution.IsOpen) return;
            state.Snapshot = snapshot;
            var root = EnsureFilter(state.Project.ProjectItems, state.Project.Object, "Dependencies");
            foreach (var name in state.Owned.Where(n => !snapshot.Dependencies.Any(d => d.Identity == n)).ToArray()) Find(root.ProjectItems, name)?.Remove();
            state.Owned = new HashSet<string>(snapshot.Dependencies.Select(d => d.Identity), StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in snapshot.Dependencies)
            {
                var group = EnsureFilter(root.ProjectItems, root.Object, dependency.Identity);
                if (dependency.Directory == null) continue;
                var manifest = Path.Combine(dependency.Directory, "Package.swift");
                var existing = Find(group.ProjectItems, "Package.swift");
                if (existing != null && !string.Equals(existing.FileNames[1], manifest, StringComparison.OrdinalIgnoreCase)) { existing.Remove(); existing = null; }
                if (existing == null) group.ProjectItems.AddFromFile(manifest);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(state.OwnershipPath));
            File.WriteAllLines(state.OwnershipPath, state.Owned);
            PrototypeLog.Write("Swift Dependencies [" + state.Project.Name + "]: " + snapshot.Dependencies.Count);
        }
        private ProjectState CreateState(Project project, string workspace)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var ownership = Path.Combine(workspace, ".vs", "VisualSwift", Path.GetFileName(project.FullName) + ".dependencies");
            var state = new ProjectState { Project = project, Workspace = workspace, OwnershipPath = ownership };
            if (File.Exists(ownership)) state.Owned = new HashSet<string>(File.ReadAllLines(ownership), StringComparer.OrdinalIgnoreCase);
            state.Watcher = new FileSystemWatcher(workspace) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler changed = (_, args) => QueueRefresh(state, args.Name);
            state.Watcher.Changed += changed;
            state.Watcher.Created += changed;
            state.Watcher.Deleted += changed;
            state.Watcher.Renamed += (_, args) => { QueueRefresh(state, args.Name); QueueRefresh(state, args.OldName); };
            state.Watcher.EnableRaisingEvents = true;
            return state;
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSSDK007", Justification = "The watcher task catches failures and is tracked by FileAndForget.")]
        private void QueueRefresh(ProjectState state, string name)
        {
            if (name != "Package.swift" && name != "Package.resolved") return;
            lock (state)
            {
                if (disposed) return;
                state.Debounce?.Cancel();
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                state.Debounce = cancellation;
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        await Task.Delay(1500, cancellation.Token);
                        await RefreshAsync(state);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception error) { PrototypeLog.Write("Swift dependency refresh: " + error.Message); }
                    finally { lock (state) { if (state.Debounce == cancellation) state.Debounce = null; } cancellation.Dispose(); }
                }).FileAndForget("VisualSwift/DependencyWatcher");
            }
        }
        private static ProjectItem Find(ProjectItems items, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (ProjectItem item in items) if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) return item;
            return null;
        }
        private static ProjectItem EnsureFilter(ProjectItems items, object parent, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var existing = Find(items, name);
            if (existing != null) return existing;
            if (parent is VCProject project) project.AddFilter(name);
            else if (parent is VCFilter filter) filter.AddFilter(name);
            else throw new InvalidOperationException("The selected project does not support Swift dependency filters.");
            return Find(items, name) ?? throw new InvalidOperationException("Visual Studio could not create the dependency node.");
        }
        private void Clear()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var window in windows.Values.ToArray()) window.Close();
            windows.Clear();
            foreach (var state in projects.Values)
            {
                lock (state) { state.Watcher?.Dispose(); state.Debounce?.Cancel(); }
            }
            projects.Clear();
        }
        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            disposed = true;
            solutionEvents.Opened -= Schedule;
            solutionEvents.ProjectAdded -= ProjectAdded;
            solutionEvents.AfterClosing -= Clear;
            documentEvents.DocumentSaved -= DocumentSaved;
            Clear();
        }
    }
}
