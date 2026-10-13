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
        private SwiftFileWatcher watcher;
        private string workspace;
        private string selectedToolchain;
        private long revision;
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
                    var source = workspace == null ? null : Path.Combine(workspace, "Package.swift");
                    return source == null || !File.Exists(source) ? Enumerable.Empty<ITestContainer>() : new[] { new SwiftTestContainer(this, source, revision) };
                }
            }
        }

        private void RuntimeChanged()
        {
            var next = SwiftRuntime.Workspace;
            string selection;
            try { selection = SwiftSettings.Default.Read(next).Path; }
            catch (Exception error) { PrototypeLog.Write("Swift Test Explorer settings: " + error.Message); return; }
            lock (gate)
            {
                if (disposed || (string.Equals(next, workspace, StringComparison.OrdinalIgnoreCase) && string.Equals(selection, selectedToolchain, StringComparison.OrdinalIgnoreCase))) return;
                watcher?.Dispose();
                watcher = null;
                workspace = next;
                selectedToolchain = selection;
                revision++;
                if (workspace != null && Directory.Exists(workspace)) watcher = new SwiftFileWatcher(workspace, _ => { Update(); return Task.CompletedTask; });
            }
            TestContainersUpdated?.Invoke(this, EventArgs.Empty);
        }

        private void Update()
        {
            lock (gate) { if (disposed) return; revision++; }
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
                watcher?.Dispose();
                watcher = null;
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
