using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace VisualSwift
{
    internal sealed class SwiftFileWatcher : IDisposable
    {
        private readonly string root;
        private readonly Func<JObject, Task> notify;
        private readonly object gate = new object();
        private readonly Dictionary<string, int> pending = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim sending = new SemaphoreSlim(1);
        private readonly FileSystemWatcher watcher;
        private readonly Timer timer;
        private bool disposed;
        private bool structureChanged;
        private bool scanned;
        public Action StructureChanged { get; set; } = delegate { };

        public SwiftFileWatcher(string workspace, Func<JObject, Task> notify)
        {
            root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            this.notify = notify;
            timer = new Timer(_ => _ = FlushSafelyAsync(), null, Timeout.Infinite, Timeout.Infinite);
            watcher = new FileSystemWatcher(workspace) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            watcher.Created += (_, e) => Change(e.FullPath, 1);
            watcher.Changed += (_, e) => Change(e.FullPath, 2);
            watcher.Deleted += (_, e) => Change(e.FullPath, 3);
            watcher.Renamed += (_, e) => { Change(e.OldFullPath, 3); Change(e.FullPath, 1); };
            watcher.Error += (_, e) => { PrototypeLog.Write("Swift file watcher recovery: " + e.GetException().Message); Rescan(); };
            watcher.EnableRaisingEvents = true;
            Rescan();
        }

        private bool Relevant(string path)
        {
            if (!Included(path)) return false;
            return path.EndsWith(".swift", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".swiftinterface", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path) == "Package.resolved" || Path.GetFileName(path) == "compile_commands.json";
        }

        private bool Included(string path)
        {
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            var parts = path.Substring(root.Length).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return !parts.Any(p => string.Equals(p, ".build", StringComparison.OrdinalIgnoreCase) || string.Equals(p, ".git", StringComparison.OrdinalIgnoreCase) || string.Equals(p, ".vs", StringComparison.OrdinalIgnoreCase));
        }

        private void Change(string path, int type)
        {
            bool rescan;
            lock (gate)
            {
                if (disposed || !Included(path)) return;
                if (Relevant(path))
                {
                    if (type == 2 && path.EndsWith(".swift", StringComparison.OrdinalIgnoreCase))
                        return;
                    if (type != 2 && path.EndsWith(".swift", StringComparison.OrdinalIgnoreCase)) structureChanged = true;
                    pending[path] = type == 2 && pending.TryGetValue(path, out var previous) && previous == 1 ? 1 : type;
                    if (type == 3) known.Remove(path); else known.Add(path);
                    timer.Change(500, Timeout.Infinite);
                    return;
                }
                rescan = type != 2 && (Directory.Exists(path) || known.Any(file => file.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
            }
            if (rescan) Rescan();
        }

        private IEnumerable<string> Files(string directory)
        {
            foreach (var file in Directory.EnumerateFiles(directory)) if (Relevant(file)) yield return file;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name == ".build" || name == ".git" || name == ".vs" || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var file in Files(child)) yield return file;
            }
        }

        private void Rescan()
        {
            try
            {
                lock (gate)
                {
                    if (disposed) return;
                    var current = new HashSet<string>(Files(root), StringComparer.OrdinalIgnoreCase);
                    if (scanned && (current.Any(file => file.EndsWith(".swift", StringComparison.OrdinalIgnoreCase) && !known.Contains(file)) || known.Any(file => file.EndsWith(".swift", StringComparison.OrdinalIgnoreCase) && !current.Contains(file)))) structureChanged = true;
                    foreach (var file in current) pending[file] = known.Contains(file) ? 2 : 1;
                    foreach (var file in known) if (!current.Contains(file)) pending[file] = 3;
                    known.Clear();
                    known.UnionWith(current);
                    scanned = true;
                    timer.Change(500, Timeout.Infinite);
                }
            }
            catch (IOException error) { PrototypeLog.Write("Swift file scan failed: " + error.Message); }
            catch (UnauthorizedAccessException error) { PrototypeLog.Write("Swift file scan failed: " + error.Message); }
        }

        private async Task FlushSafelyAsync()
        {
            try { await FlushAsync().ConfigureAwait(false); }
            catch (Exception error) { PrototypeLog.Write("Swift file notification failed: " + error.Message); }
        }

        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            if (!await sending.WaitAsync(3000, cancellationToken).ConfigureAwait(false))
                return;
            try
            {
                JArray changes;
                bool refresh;
                lock (gate)
                {
                    if (disposed || pending.Count == 0) return;
                    changes = new JArray(pending.Select(p => new JObject { ["uri"] = new Uri(p.Key).AbsoluteUri, ["type"] = p.Value }));
                    pending.Clear();
                    refresh = structureChanged;
                    structureChanged = false;
                }
                await notify(new JObject { ["changes"] = changes }).ConfigureAwait(false);
                PrototypeLog.Write("Swift file changes forwarded: " + changes.Count);
                if (refresh) StructureChanged?.Invoke();
            }
            finally { sending.Release(); }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                watcher.Dispose();
                timer.Dispose();
                pending.Clear();
            }
        }
    }
}
