using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestWindow.Extensibility;
using Microsoft.VisualStudio.TestWindow.Extensibility.Model;

namespace VisualSwift
{
    [Export(typeof(ITestContainerDiscoverer))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class SwiftTestContainers : ITestContainerDiscoverer, IDisposable
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, ProjectTests> projects = new Dictionary<string, ProjectTests>(StringComparer.OrdinalIgnoreCase);
        private sealed class ProjectTests { internal SwiftFileWatcher Watcher; internal string Settings; internal long Revision; }
        private bool disposed;
        public Uri ExecutorUri { get; } = new Uri("executor://visualswift/swiftpm/v1");
        public event EventHandler TestContainersUpdated;

        [ImportingConstructor]
        public SwiftTestContainers()
        {
            SwiftRuntime.Changed += RuntimeChanged;
            RuntimeChanged();
        }

        public IEnumerable<ITestContainer> TestContainers
        {
            get
            {
                lock (gate)
                {
                    return projects.Where(project => File.Exists(Path.Combine(project.Key, "Package.swift"))).Select(project => (ITestContainer)new SwiftTestContainer(this, Path.Combine(project.Key, "Package.swift"), project.Value.Revision)).ToArray();
                }
            }
        }

        private void RuntimeChanged()
        {
            var next = SwiftRuntime.Workspaces;
            var changed = false;
            lock (gate)
            {
                if (disposed) return;
                foreach (var removed in projects.Keys.Where(root => !next.Contains(root, StringComparer.OrdinalIgnoreCase)).ToArray()) { projects[removed].Watcher?.Dispose(); projects.Remove(removed); changed = true; }
                foreach (var root in next)
                {
                    try
                    {
                        var selection = SwiftSettings.Default.Read(root).Path + "|" + File.GetLastWriteTimeUtc(SwiftProjectSettings.FilePath(root)).Ticks;
                        if (!projects.TryGetValue(root, out var project))
                        {
                            projects[root] = project = new ProjectTests();
                            if (Directory.Exists(root)) project.Watcher = new SwiftFileWatcher(root, _ => { Update(root); return Task.CompletedTask; });
                        }
                        else if (selection == project.Settings) continue;
                        project.Settings = selection; project.Revision++; changed = true;
                    }
                    catch (Exception error) { PrototypeLog.Write("Swift Test Explorer settings [" + root + "]: " + error.Message); }
                }
            }
            if (changed) TestContainersUpdated?.Invoke(this, EventArgs.Empty);
        }

        private void Update(string root)
        {
            lock (gate) { if (disposed || !projects.TryGetValue(root, out var project)) return; project.Revision++; }
            PrototypeLog.Write("Swift Test Explorer containers updated.");
            TestContainersUpdated?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                SwiftRuntime.Changed -= RuntimeChanged;
                foreach (var project in projects.Values) project.Watcher?.Dispose();
                projects.Clear();
            }
        }
    }

    internal sealed class SwiftTestContainer : ITestContainer
    {
        private readonly long revision;
        public SwiftTestContainer(ITestContainerDiscoverer discoverer, string source, long revision)
        { Discoverer = discoverer; Source = source; this.revision = revision; }
        public ITestContainerDiscoverer Discoverer { get; }
        public string Source { get; }
        public IEnumerable<Guid> DebugEngines => Enumerable.Empty<Guid>();
        public FrameworkVersion TargetFramework => FrameworkVersion.None;
        public Architecture TargetPlatform => Architecture.Default;
        public bool IsAppContainerTestContainer => false;
        public IDeploymentData DeployAppContainer() => null;
        public ITestContainer Snapshot() => new SwiftTestContainer(Discoverer, Source, revision);
        public int CompareTo(ITestContainer other)
        {
            if (other == null) return 1;
            var sourceOrder = StringComparer.OrdinalIgnoreCase.Compare(Source, other.Source);
            if (sourceOrder != 0) return sourceOrder;
            return other is SwiftTestContainer container ? revision.CompareTo(container.revision) : 1;
        }
    }
}
