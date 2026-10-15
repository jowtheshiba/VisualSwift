using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.PlatformUI;
using Newtonsoft.Json.Linq;

namespace VisualSwift
{
    internal sealed class SwiftHierarchyControl : UserControl
    {
        private readonly ComboBox direction = new ComboBox { MinWidth = 180, Margin = new Thickness(0, 0, 8, 8) };
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
        private readonly TreeView tree = new TreeView();
        private JArray roots;
        private Func<string, object, Task<JToken>> request;
        private int generation;
        private bool types;
        private string sourceUri;
        private int sourceLine, sourceCharacter;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSSDK007", Justification = "Refresh tasks are observed with FileAndForget and ShowAsync handles request failures.")]
        public SwiftHierarchyControl()
        {
            var panel = new DockPanel();
            var header = new StackPanel();
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
            toolbar.Children.Add(direction);
            var refresh = new Button { Content = "Refresh", Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(8, 3, 8, 3) };
            toolbar.Children.Add(refresh);
            header.Children.Add(toolbar);
            header.Children.Add(status);
            DockPanel.SetDock(header, Dock.Top);
            panel.Children.Add(header);
            panel.Children.Add(tree);
            Content = panel;
            tree.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            tree.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var itemStyle = new Style(typeof(TreeViewItem));
            itemStyle.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            tree.Resources[typeof(TreeViewItem)] = itemStyle;
            direction.SetResourceReference(StyleProperty, VsResourceKeys.ComboBoxStyleKey);
            refresh.SetResourceReference(StyleProperty, VsResourceKeys.ButtonStyleKey);
            direction.SelectionChanged += (_, __) => Render();
            refresh.Click += (_, __) => ThreadHelper.JoinableTaskFactory.RunAsync(async () => { if (sourceUri != null) await ShowAsync(sourceUri, sourceLine, sourceCharacter, types); }).FileAndForget("VisualSwift/HierarchyRefresh");
            tree.MouseDoubleClick += Navigate;
            tree.KeyDown += (_, e) => { ThreadHelper.ThrowIfNotOnUIThread(); if (e.Key == Key.Enter) Navigate(tree, null); };
            Unloaded += (_, __) => generation++;
            status.Text = "Right-click a Swift symbol and choose Swift Call Hierarchy or Swift Type Hierarchy.";
        }

        internal async Task ShowAsync(string uri, int line, int character, bool showTypes)
        {
            var current = ++generation;
            types = showTypes;
            sourceUri = uri; sourceLine = line; sourceCharacter = character;
            request = SwiftRuntime.Request;
            roots = null;
            tree.Items.Clear();
            direction.Items.Clear();
            direction.Items.Add(types ? "Base Types" : "Incoming Calls");
            direction.Items.Add(types ? "Derived Types" : "Outgoing Calls");
            direction.SelectedIndex = 0;
            status.Text = "Loading hierarchy…";
            try
            {
                if (request == null || SwiftRuntime.State != "Running") throw new InvalidOperationException("Open a Swift file and wait for SourceKit-LSP to start.");
                var result = await request(types ? "textDocument/prepareTypeHierarchy" : "textDocument/prepareCallHierarchy", new { textDocument = new { uri }, position = new { line, character } });
                if (!IsCurrent(current)) return;
                roots = result as JArray ?? new JArray();
                Render();
            }
            catch (Exception error) { if (current == generation) status.Text = "Hierarchy unavailable: " + error.Message; PrototypeLog.Write("Swift hierarchy: " + error); }
        }

        private bool IsCurrent(int current) => current == generation && ReferenceEquals(request, SwiftRuntime.Request);

        private void Render()
        {
            if (roots == null) return;
            generation++;
            tree.Items.Clear();
            if (!ReferenceEquals(request, SwiftRuntime.Request)) { status.Text = "The language server changed. Run the hierarchy command again."; return; }
            status.Text = roots.Count == 0 ? "No symbol found. Select a method or type; indexed results may require a build." : "Expand a symbol to load related symbols. Double-click a symbol or call site to open its source.";
            foreach (var item in roots.OfType<JObject>()) tree.Items.Add(Node(item, new string[0], generation));
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSSDK007", Justification = "Expansion tasks are observed with FileAndForget and handle request failures locally.")]
        private TreeViewItem Node(JObject item, string[] ancestors, int current)
        {
            var key = (string)item["uri"] + ":" + item["selectionRange"]?.ToString(Newtonsoft.Json.Formatting.None) + ":" + (string)item["name"];
            var node = new TreeViewItem { Header = (string)item["name"] + (item["detail"] == null ? "" : " — " + (string)item["detail"]), Tag = new Location((string)item["uri"], item["selectionRange"]?["start"]) };
            node.ToolTip = (string)item["uri"];
            if (ancestors.Contains(key)) { node.Header += " (recursive)"; return node; }
            node.Items.Add(new TreeViewItem { Header = "Expand to load…" });
            var loaded = false;
            node.Expanded += (_, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, node) || loaded) return;
                loaded = true;
                var selectedDirection = direction.SelectedIndex;
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        var method = types ? (selectedDirection == 0 ? "typeHierarchy/supertypes" : "typeHierarchy/subtypes") : (selectedDirection == 0 ? "callHierarchy/incomingCalls" : "callHierarchy/outgoingCalls");
                        var result = await request(method, new { item });
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (!IsCurrent(current)) return;
                        var sites = node.Items.OfType<TreeViewItem>().Where(child => child.Tag is Location).ToArray();
                        node.Items.Clear();
                        foreach (var site in sites) node.Items.Add(site);
                        foreach (var entry in (result as JArray ?? new JArray()).OfType<JObject>())
                        {
                            var symbol = types ? entry : entry[selectedDirection == 0 ? "from" : "to"] as JObject;
                            if (symbol == null) continue;
                            var child = Node(symbol, ancestors.Concat(new[] { key }).ToArray(), current);
                            if (!types && entry["fromRanges"] is JArray ranges)
                            {
                                var sourceUri = (string)(selectedDirection == 0 ? symbol : item)["uri"];
                                foreach (var range in ranges)
                                    child.Items.Add(new TreeViewItem { Header = "Call site: " + Path.GetFileName(new Uri(sourceUri).LocalPath) + ":" + ((int)range["start"]["line"] + 1), Tag = new Location(sourceUri, range["start"]) });
                            }
                            node.Items.Add(child);
                        }
                        if (node.Items.Count == 0) node.Items.Add(new TreeViewItem { Header = "No results. The symbol may not be indexed yet.", IsEnabled = false });
                    }
                    catch (Exception error)
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (!IsCurrent(current)) return;
                        loaded = false;
                        node.Items.Clear();
                        node.Items.Add(new TreeViewItem { Header = "Unable to load. Collapse and expand to retry." });
                        status.Text = error.Message;
                        PrototypeLog.Write("Swift hierarchy: " + error);
                    }
                }).FileAndForget("VisualSwift/Hierarchy");
            };
            return node;
        }

        private void Navigate(object sender, MouseButtonEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!((tree.SelectedItem as TreeViewItem)?.Tag is Location location)) return;
            try
            {
                var uri = new Uri(location.Uri);
                if (!uri.IsFile) { status.Text = "This symbol has no local source file."; return; }
                VsShellUtilities.OpenDocument(SwiftPackage.Instance, uri.LocalPath);
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                (dte?.ActiveDocument?.Selection as EnvDTE.TextSelection)?.MoveToLineAndOffset(location.Line + 1, location.Character + 1);
                if (e != null) e.Handled = true;
            }
            catch (Exception error) { status.Text = "Cannot open source: " + error.Message; }
        }

        private sealed class Location
        {
            public string Uri { get; }
            public int Line { get; }
            public int Character { get; }
            public Location(string uri, JToken position) { Uri = uri; Line = (int?)position?["line"] ?? 0; Character = (int?)position?["character"] ?? 0; }
        }
    }
}
