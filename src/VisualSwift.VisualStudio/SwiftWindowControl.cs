using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace VisualSwift
{
    internal sealed class SwiftWindowControl : UserControl
    {
        private readonly TextBox toolchainPath = new TextBox { MinWidth = 220 };
        private readonly ComboBox scope = new ComboBox { MinWidth = 120 };
        private readonly TextBlock selection = Label();
        private readonly TextBlock validation = Label();
        private readonly TextBlock server = Label();
        private readonly TextBlock active = Label();
        private readonly TextBlock package = Label();
        private readonly TextBlock metadata = Label();
        private readonly TextBlock message = Label();
        private readonly StackPanel actions = new StackPanel();
        private readonly Button cancel = new Button { Content = "Cancel", IsEnabled = false, Margin = new Thickness(0, 8, 0, 0) };
        private CancellationTokenSource operation;

        public SwiftWindowControl()
        {
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            SetResourceReference(FontFamilyProperty, VsFonts.EnvironmentFontFamilyKey);
            SetResourceReference(FontSizeProperty, VsFonts.EnvironmentFontSizeKey);
            var root = new StackPanel { Margin = new Thickness(12), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch };
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            root.Children.Add(actions);
            Heading(actions, "Toolchain");
            actions.Children.Add(selection);
            actions.Children.Add(new TextBlock { Text = "Toolchain root or bin directory", Margin = new Thickness(0, 6, 0, 4) });
            toolchainPath.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            toolchainPath.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            actions.Children.Add(toolchainPath);
            var choose = Row(actions);
            Button(choose, "Browse...", BrowseAsync);
            Button(choose, "Validate", ValidateAsync);
            scope.Items.Add("Global"); scope.Items.Add("Workspace"); scope.SelectedIndex = 0;
            scope.Margin = new Thickness(0, 6, 6, 0);
            choose.Children.Add(scope);
            Button(choose, "Apply", ApplyAsync);
            Button(choose, "Use Global", UseGlobalAsync);
            actions.Children.Add(validation);
            Heading(actions, "Language Server");
            actions.Children.Add(server);
            actions.Children.Add(active);
            var language = Row(actions);
            Button(language, "Restart", _ => SwiftPackage.RestartAsync());
            Button(language, "Show Output", _ => SwiftPackage.Instance.ShowOutputAsync());
            Heading(actions, "Package");
            actions.Children.Add(package);
            actions.Children.Add(metadata);
            var packages = Row(actions);
            Button(packages, "Refresh Package", RefreshPackageAsync);
            Button(packages, "Open Package.swift", OpenManifestAsync);
            root.Children.Add(message);
            root.Children.Add(cancel);
            cancel.Click += (_, __) => operation?.Cancel();
            Loaded += OnLoaded;
            Unloaded += (_, __) => { SwiftRuntime.Changed -= RuntimeChanged; operation?.Cancel(); };
        }

        private static TextBlock Label() => new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        private static void Heading(Panel parent, string title) => parent.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 12, 0, 6) });
        private static WrapPanel Row(Panel parent) { var row = new WrapPanel(); parent.Children.Add(row); return row; }
        private void Button(Panel parent, string title, Func<CancellationToken, Task> action)
        {
            var button = new Button { Content = title, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 6, 6, 0) };
            button.Tag = action;
            button.Click += OnButtonClick;
            parent.Children.Add(button);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WPF Loaded requires a void callback; RunAsync handles operation errors.")]
        private async void OnLoaded(object sender, RoutedEventArgs args)
        {
            SwiftRuntime.Changed -= RuntimeChanged;
            SwiftRuntime.Changed += RuntimeChanged;
            await RunAsync(RefreshAsync);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "WPF Click requires a void callback; RunAsync handles operation errors.")]
        private async void OnButtonClick(object sender, RoutedEventArgs args) => await RunAsync((Func<CancellationToken, Task>)((Button)sender).Tag);

        private async Task RunAsync(Func<CancellationToken, Task> action)
        {
            if (operation != null) return;
            using (var cancellation = new CancellationTokenSource())
            {
                operation = cancellation;
                actions.IsEnabled = false;
                cancel.IsEnabled = true;
                message.Text = "Working...";
                try { await action(cancellation.Token); if (!cancellation.IsCancellationRequested) message.Text = "Ready."; }
                catch (OperationCanceledException) { message.Text = "Cancelled."; }
                catch (Exception error) { message.Text = error.Message; PrototypeLog.Write("Swift window: " + error); }
                finally { operation = null; actions.IsEnabled = true; cancel.IsEnabled = false; UpdateStatus(); }
            }
        }

        private async Task RefreshAsync(CancellationToken token)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            SwiftRuntime.SetWorkspace(SwiftWorkspace.Current() ?? SwiftRuntime.Workspace);
            var workspace = SwiftRuntime.Workspace;
            var selected = await Task.Run(() => SwiftSettings.Default.Read(workspace), token);
            scope.SelectedIndex = selected.Scope == "Workspace" ? 1 : 0;
            var resolved = await Task.Run(() => Toolchain.Resolve(workspace), token);
            toolchainPath.Text = resolved.BinDirectory;
            await ValidateToolchainAsync(resolved, token);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            await RefreshPackageAsync(token);
        }

        private Task BrowseAsync(CancellationToken token)
        {
            var dialog = new OpenFileDialog { Title = "Select Swift Compiler", Filter = "Swift compiler (swift.exe)|swift.exe", CheckFileExists = true };
            if (dialog.ShowDialog() == true) toolchainPath.Text = Path.GetDirectoryName(dialog.FileName);
            return Task.CompletedTask;
        }

        private async Task ValidateAsync(CancellationToken token)
        {
            var candidate = toolchainPath.Text;
            var resolved = await Task.Run(() => Toolchain.FromPath(candidate), token);
            await ValidateToolchainAsync(resolved, token);
        }

        private async Task<string> ValidateToolchainAsync(Toolchain toolchain, CancellationToken token)
        {
            validation.Text = "Checking compiler and SDK...";
            var version = await toolchain.ValidateAsync(token);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            var sdk = Environment.GetEnvironmentVariable("SDKROOT");
            validation.Text = version + Environment.NewLine + "SDK: " + sdk;
            var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(DTE)) as DTE;
            var configuration = dte?.Solution?.SolutionBuild?.ActiveConfiguration as EnvDTE80.SolutionConfiguration2;
            var platform = configuration?.PlatformName;
            if ((version.Contains("aarch64-") && string.Equals(platform, "x64", StringComparison.OrdinalIgnoreCase))
                || (version.Contains("x86_64-") && string.Equals(platform, "ARM64", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The solution platform " + platform + " does not match the Swift toolchain architecture. Select the matching ARM64 or x64 platform before building.");
            Toolchain selected = null;
            try { selected = await Task.Run(() => Toolchain.Resolve(SwiftRuntime.Workspace), token); }
            catch (InvalidOperationException) { }
            if (selected != null && string.Equals(selected.BinDirectory, toolchain.BinDirectory, StringComparison.OrdinalIgnoreCase)) SwiftRuntime.SetVersion(version);
            return version;
        }

        private async Task ApplyAsync(CancellationToken token)
        {
            var path = toolchainPath.Text;
            var workspaceScope = scope.SelectedIndex == 1;
            var workspace = SwiftRuntime.Workspace;
            var toolchain = await Task.Run(() => Toolchain.FromPath(path), token);
            var version = await ValidateToolchainAsync(toolchain, token);
            await Task.Run(() => SwiftSettings.Default.Save(toolchain.BinDirectory, workspace, workspaceScope), token);
            var selected = await Task.Run(() => Toolchain.Resolve(workspace), token);
            SwiftRuntime.SetVersion(string.Equals(selected.BinDirectory, toolchain.BinDirectory, StringComparison.OrdinalIgnoreCase) ? version : null);
            PrototypeLog.Write("Saved " + (workspaceScope ? "workspace" : "global") + " toolchain: " + toolchain.BinDirectory);
        }

        private async Task UseGlobalAsync(CancellationToken token)
        {
            var workspace = SwiftRuntime.Workspace;
            await Task.Run(() => SwiftSettings.Default.ClearWorkspace(workspace), token);
            await RefreshAsync(token);
        }

        private async Task RefreshPackageAsync(CancellationToken token)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            SwiftRuntime.SetWorkspace(SwiftWorkspace.Current() ?? SwiftRuntime.Workspace);
            var workspace = SwiftRuntime.Workspace;
            if (workspace == null) { metadata.Text = "Open a Swift package and a Swift source file."; return; }
            var toolchain = await Task.Run(() => Toolchain.Resolve(workspace), token);
            var result = await SwiftProcess.RunAsync(toolchain, "package --package-path \"" + workspace + "\" describe --type json", workspace, token);
            var description = JObject.Parse(result);
            var products = (description["products"] as JArray ?? new JArray()).Select(product => product.Value<string>("name") + " (" + (product["type"] as JObject)?.Properties().FirstOrDefault()?.Name + ")");
            var targets = (description["targets"] as JArray ?? new JArray()).Select(target => target.Value<string>("name") + " (" + target.Value<string>("type") + ")");
            metadata.Text = "Name: " + description.Value<string>("name") + Environment.NewLine + "Products: " + string.Join(", ", products) + Environment.NewLine + "Targets: " + string.Join(", ", targets);
        }

        private async Task OpenManifestAsync(CancellationToken token)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            var workspace = SwiftRuntime.Workspace;
            if (workspace == null) throw new InvalidOperationException("Open a Swift package first.");
            VsShellUtilities.OpenDocument(SwiftPackage.Instance, Path.Combine(workspace, "Package.swift"));
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD001", Justification = "Nonblocking WPF dispatch keeps updates tied to this control's dispatcher lifetime.")]
        private void RuntimeChanged()
        {
            if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(new Action(UpdateStatus));
        }

        private void UpdateStatus()
        {
            try
            {
                var workspace = SwiftRuntime.Workspace;
                var selected = SwiftSettings.Default.Read(workspace);
                var bin = Toolchain.Resolve(workspace).BinDirectory;
                selection.Text = "Selected: " + bin + Environment.NewLine + "Source: " + selected.Scope;
                server.Text = "Status: " + SwiftRuntime.State + (SwiftRuntime.Error == null ? "" : Environment.NewLine + SwiftRuntime.Error);
                active.Text = "Active: " + (SwiftRuntime.ActiveBin ?? "None");
                if (SwiftRuntime.ActiveBin != null && (!string.Equals(bin, SwiftRuntime.ActiveBin, StringComparison.OrdinalIgnoreCase) || !string.Equals(workspace, SwiftRuntime.ActiveWorkspace, StringComparison.OrdinalIgnoreCase)))
                    active.Text += Environment.NewLine + "Restart required to use the selected toolchain/package.";
                package.Text = workspace == null ? "Package: None" : "Package: " + Path.Combine(workspace, "Package.swift");
            }
            catch (Exception error) { selection.Text = "Toolchain unavailable: " + error.Message; }
        }
    }
}
