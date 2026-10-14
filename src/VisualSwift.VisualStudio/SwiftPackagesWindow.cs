using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.PlatformUI;

namespace VisualSwift
{
    internal static class SwiftPackageTheme
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        private static void Caption(Window window)
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var color = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            var dark = color.R * 0.299 + color.G * 0.587 + color.B * 0.114 < 128 ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSSDK007", Justification = "The theme event task is tracked by FileAndForget.")]
        public static void Apply(Window window)
        {
            window.SourceInitialized += (_, __) => Caption(window);
            ThemeChangedEventHandler changed = _ => ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Caption(window);
            }).FileAndForget("VisualSwift/PackageTheme");
            VSColorTheme.ThemeChanged += changed;
            window.Closed += (_, __) => VSColorTheme.ThemeChanged -= changed;
            window.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            window.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            foreach (var pair in new[] { new object[] { typeof(Button), VsResourceKeys.ThemedDialogButtonStyleKey }, new object[] { typeof(TextBox), VsResourceKeys.ThemedDialogTextBoxStyleKey }, new object[] { typeof(ComboBox), VsResourceKeys.ThemedDialogComboBoxStyleKey } })
            {
                var style = window.TryFindResource(pair[1]) as Style;
                if (style != null) window.Resources[pair[0]] = style;
            }
            foreach (var type in new[] { typeof(ListView), typeof(ListBox), typeof(CheckBox), typeof(GridViewColumnHeader) })
            {
                var style = new Style(type);
                style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowBackgroundBrushKey)));
                style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
                window.Resources[type] = style;
            }
        }
    }
    internal sealed class SwiftPackagesWindow : DialogWindow
    {
        private readonly string workspace;
        private readonly Func<Task> refreshTree;
        private readonly ListView dependencies = new ListView();
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly Button cancel = new Button { Content = "Cancel Operation", Margin = new Thickness(4), IsEnabled = false };
        private SwiftPackageSnapshot snapshot;
        private CancellationTokenSource operation;
        private bool closed;

        public SwiftPackagesWindow(string projectName, string workspace, Func<Task> refreshTree)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.workspace = workspace;
            this.refreshTree = refreshTree;
            Title = projectName + " — Swift Packages";
            Width = 940;
            Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            new System.Windows.Interop.WindowInteropHelper(this).Owner = ((EnvDTE.DTE)Package.GetGlobalService(typeof(EnvDTE.DTE))).MainWindow.HWnd;
            var panel = new DockPanel { Margin = new Thickness(16) };
            var heading = new TextBlock { Text = workspace, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(heading, Dock.Top);
            panel.Children.Add(heading);
            DockPanel.SetDock(actions, Dock.Top);
            panel.Children.Add(actions);
            Button("Add Swift Package…", AddAsync);
            Button("Refresh", RefreshAsync);
            Button("Resolve", token => ResolveAsync(token));
            Button("Update Selected", token => UpdateAsync(false, token));
            Button("Update All", token => UpdateAsync(true, token));
            Button("Remove Selected…", RemoveAsync);
            Button("Open Folder", token =>
            {
                var selected = dependencies.SelectedItem as SwiftDependency ?? throw new InvalidOperationException("Select a package to open.");
                if (selected.Directory == null) throw new InvalidOperationException("Resolve this package before opening its folder.");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", SwiftPackages.Quote(selected.Directory)) { UseShellExecute = true });
                return Task.CompletedTask;
            });
            var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            bottom.Children.Add(status);
            cancel.Click += (_, __) => operation?.Cancel();
            bottom.Children.Add(cancel);
            DockPanel.SetDock(bottom, Dock.Bottom);
            panel.Children.Add(bottom);
            var view = new GridView();
            foreach (var column in new[] { new[] { "Package", "Identity" }, new[] { "Source", "Source" }, new[] { "Requirement", "Requirement" }, new[] { "Resolved", "Version" }, new[] { "Status", "Status" }, new[] { "Location", "Directory" } })
                view.Columns.Add(new GridViewColumn { Header = column[0], DisplayMemberBinding = new System.Windows.Data.Binding(column[1]), Width = column[1] == "Source" ? 300 : 125 });
            dependencies.View = view;
            panel.Children.Add(dependencies);
            Content = panel;
            SwiftPackageTheme.Apply(this);
            Closed += (_, __) => { closed = true; operation?.Cancel(); };
        }

        public Task InitializeAsync(string action, string identity) => PerformAsync(async token =>
        {
            await RefreshAsync(token);
            if (identity != null) dependencies.SelectedItem = snapshot.Dependencies.FirstOrDefault(d => d.Identity == identity);
            if (action == "add") await AddAsync(token);
            else if (action == "resolve") await ResolveAsync(token);
            else if (action == "update") await UpdateAsync(false, token);
            else if (action == "updateAll") await UpdateAsync(true, token);
            else if (action == "remove") await RemoveAsync(token);
        });

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSSDK007", Justification = "The event task catches errors and is tracked by FileAndForget.")]
        private void Button(string text, Func<CancellationToken, Task> action)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 12), Padding = new Thickness(8, 4, 8, 4) };
            button.Click += (_, __) => ThreadHelper.JoinableTaskFactory.RunAsync(() => PerformAsync(action)).FileAndForget("VisualSwift/Packages");
            actions.Children.Add(button);
        }
        private async Task PerformAsync(Func<CancellationToken, Task> action)
        {
            if (operation != null || closed) return;
            using (var cancellation = new CancellationTokenSource())
            {
                operation = cancellation;
                actions.IsEnabled = false;
                cancel.IsEnabled = true;
                status.Text = "Working…";
                try { await action(cancellation.Token); if (!closed) status.Text = "Ready — " + snapshot.Dependencies.Count + " direct dependencies"; }
                catch (OperationCanceledException) { if (!closed) status.Text = "Operation cancelled."; }
                catch (Exception error) { PrototypeLog.Write("Swift Packages [" + workspace + "]: " + error); if (!closed) status.Text = error.Message; }
                finally { operation = null; if (!closed) { actions.IsEnabled = true; cancel.IsEnabled = false; } }
            }
        }
        private async Task RefreshAsync(CancellationToken token)
        {
            snapshot = await SwiftPackages.ReadAsync(workspace, token);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            if (closed) return;
            dependencies.ItemsSource = snapshot.Dependencies;
        }
        private async Task ResolveAsync(CancellationToken token)
        {
            status.Text = "Resolving packages…";
            await SwiftPackages.ResolveAsync(workspace, token);
            await FinishAsync(token);
        }
        private async Task UpdateAsync(bool all, CancellationToken token)
        {
            var selected = all ? null : (dependencies.SelectedItem as SwiftDependency ?? throw new InvalidOperationException("Select a package to update."));
            if (selected?.Local == true) throw new InvalidOperationException("Local path packages use the files on disk and do not have a remote version to update.");
            status.Text = all ? "Updating all packages…" : "Updating " + selected.Identity + "…";
            await SwiftPackages.UpdateAsync(workspace, selected?.Identity, token);
            await FinishAsync(token);
        }
        private async Task FinishAsync(CancellationToken token)
        {
            await RefreshAsync(token);
            await refreshTree();
        }
        private async Task RemoveAsync(CancellationToken token)
        {
            var selected = dependencies.SelectedItem as SwiftDependency ?? throw new InvalidOperationException("Select a package to remove.");
            await SwiftPackages.LoadProductsAsync(workspace, selected, token);
            var edit = await Task.Run(() => SwiftPackages.PreviewRemove(workspace, selected), token);
            if (!Preview(edit, "Remove " + selected.Identity)) return;
            await SwiftPackages.ApplyAsync(edit, token, new Progress<string>(message => { if (!closed) status.Text = message; }));
            await FinishAsync(token);
        }
        private async Task AddAsync(CancellationToken token)
        {
            var dialog = new AddSwiftPackageWindow(workspace, snapshot.Targets) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            var request = dialog.Request;
            var identity = dialog.Dependency.Identity;
            if (snapshot.Dependencies.Any(d => string.Equals(d.Identity, identity, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("This package is already a dependency of the project.");
            var edit = await SwiftPackages.PreviewAddAsync(workspace, request, identity, token);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            status.Text = "Adding " + identity + " to " + request.Target + "…";
            await SwiftPackages.ApplyAsync(edit, token, new Progress<string>(message => { if (!closed) status.Text = message; }));
            await FinishAsync(token);
        }
        private bool Preview(SwiftPackageEdit edit, string title)
        {
            var window = new DialogWindow { Title = title + " — " + Path.GetFileName(workspace), Owner = this, Width = 950, Height = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new DockPanel { Margin = new Thickness(16) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var apply = new Button { Content = "Apply and Resolve", IsDefault = true, Margin = new Thickness(8), Padding = new Thickness(12, 6, 12, 6) };
            apply.Click += (_, __) => window.DialogResult = true;
            buttons.Children.Add(apply);
            buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(8), Padding = new Thickness(12, 6, 12, 6) });
            DockPanel.SetDock(buttons, Dock.Bottom);
            panel.Children.Add(buttons);
            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            var before = PreviewText("Current Package.swift\n\n" + edit.Original);
            var after = PreviewText("Proposed Package.swift\n\n" + edit.Updated);
            Grid.SetColumn(after, 1);
            columns.Children.Add(before);
            columns.Children.Add(after);
            panel.Children.Add(columns);
            window.Content = panel;
            SwiftPackageTheme.Apply(window);
            return window.ShowDialog() == true;
        }
        private static TextBox PreviewText(string text) => new TextBox { Text = text, IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(4), TextWrapping = TextWrapping.NoWrap };
    }

    internal sealed class AddSwiftPackageWindow : DialogWindow
    {
        private readonly string workspace;
        private readonly TextBox source = new TextBox();
        private readonly ComboBox requirement = new ComboBox { ItemsSource = new[] { "Latest Stable", "From Version", "Exact Version", "Version Range", "Branch", "Revision", "Local Path" }, SelectedIndex = 0 };
        private readonly TextBox value = new TextBox { IsEnabled = false };
        private readonly ComboBox product = new ComboBox { IsEnabled = false };
        private readonly ComboBox target;
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly Button inspect = new Button { Content = "Load Products", Margin = new Thickness(0, 12, 0, 12) };
        private readonly Button add = new Button { Content = "Add", IsDefault = true, IsEnabled = false, Margin = new Thickness(8) };
        private CancellationTokenSource loading;
        public SwiftDependency Dependency { get; private set; }
        public SwiftPackageRequest Request => new SwiftPackageRequest { Source = source.Text.Trim(), Requirement = requirement.SelectedItem as string == "Latest Stable" && Dependency?.MinimumVersion != null ? "From Version" : (string)requirement.SelectedItem, Value = requirement.SelectedItem as string == "Latest Stable" ? Dependency?.MinimumVersion : value.Text.Trim(), Product = (string)product.SelectedItem, Target = (string)target.SelectedItem };
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSSDK007", Justification = "The inspection task catches errors and is tracked by FileAndForget.")]
        public AddSwiftPackageWindow(string workspace, string[] targets)
        {
            this.workspace = workspace;
            Title = "Add Swift Package — " + Path.GetFileName(workspace);
            Width = 560;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            target = new ComboBox { ItemsSource = targets, SelectedIndex = targets.Length > 0 ? 0 : -1 };
            var panel = new StackPanel { Margin = new Thickness(20) };
            Field(panel, "Git repository URL or local package path", source);
            Field(panel, "Requirement", requirement);
            Field(panel, "Version / branch / revision (range: lower,upper)", value);
            panel.Children.Add(inspect);
            Field(panel, "Library product", product);
            Field(panel, "Project target", target);
            panel.Children.Add(status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(add);
            buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(8) });
            panel.Children.Add(buttons);
            Content = panel;
            SwiftPackageTheme.Apply(this);
            source.TextChanged += (_, __) => Invalidate();
            value.TextChanged += (_, __) => Invalidate();
            requirement.SelectionChanged += (_, __) => { value.IsEnabled = requirement.SelectedItem as string != "Local Path" && requirement.SelectedItem as string != "Latest Stable"; Invalidate(); };
            inspect.Click += (_, __) => ThreadHelper.JoinableTaskFactory.RunAsync(InspectAsync).FileAndForget("VisualSwift/InspectPackage");
            add.Click += (_, __) => { if (product.SelectedItem != null && target.SelectedItem != null) DialogResult = true; };
            Closed += (_, __) => loading?.Cancel();
        }
        private void Invalidate() { Dependency = null; add.IsEnabled = false; product.IsEnabled = false; loading?.Cancel(); }
        private async Task InspectAsync()
        {
            if (loading != null) return;
            using (var cancellation = new CancellationTokenSource())
            {
                loading = cancellation;
                inspect.IsEnabled = false;
                status.Text = "Resolving package and loading products…";
                try
                {
                    var dependency = await SwiftPackages.InspectAsync(workspace, Request, cancellation.Token, new Progress<string>(message => status.Text = message));
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation.Token);
                    Dependency = dependency;
                    product.ItemsSource = dependency.Products;
                    product.SelectedIndex = 0;
                    product.IsEnabled = true;
                    add.IsEnabled = target.SelectedItem != null;
                    status.Text = dependency.PackageName + (dependency.Version == null ? "" : " — resolved " + dependency.Version) + "\nClick Add to install into " + Path.Combine(workspace, ".build", "checkouts") + ". Local path packages stay in their original folder.";
                }
                catch (OperationCanceledException) { status.Text = "Cancelled."; }
                catch (Exception error) { status.Text = error.Message; PrototypeLog.Write("Swift package inspection failed: " + error); }
                finally { loading = null; inspect.IsEnabled = true; }
            }
        }
        private static void Field(StackPanel panel, string label, Control control)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
            panel.Children.Add(control);
        }
    }
}
