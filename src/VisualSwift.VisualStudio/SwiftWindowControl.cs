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
        private readonly TextBlock formatter = Label();
        private readonly CheckBox inlayEnabled = new CheckBox { Content = "Show inlay hints", Margin = new Thickness(0, 8, 0, 4) };
        private readonly CheckBox inlayTypes = new CheckBox { Content = "Inferred types", Margin = new Thickness(0, 4, 0, 4) };
        private readonly CheckBox inlayParameters = new CheckBox { Content = "Parameter names (when provided by the toolchain)", Margin = new Thickness(0, 4, 0, 4) };
        private readonly ComboBox inlayScope = new ComboBox { MinWidth = 120 };
        private readonly TextBlock package = Label();
        private readonly TextBlock metadata = Label();
        private readonly TextBlock message = Label();
        private readonly ComboBox debugProduct = new ComboBox { MinWidth = 180 };
        private readonly TextBox debugArguments = new TextBox { Text = "[]" };
        private readonly CheckBox stopOnEntry = new CheckBox { Content = "Stop on entry", Margin = new Thickness(0, 8, 0, 8) };
        private readonly CheckBox useF5 = new CheckBox { Content = "Use Swift debugger for F5 in this package", Margin = new Thickness(0, 8, 0, 8) };
        private readonly Grid actions = new Grid();
        private readonly TextBlock summary = Label();
        private readonly ListBox navigation = new ListBox { MinWidth = 150, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 12, 0) };
        private readonly TextBox output = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly Button cancel = new Button { Content = "Cancel", IsEnabled = false, Margin = new Thickness(0, 8, 0, 0) };
        private CancellationTokenSource operation;
        private string hintsWorkspace;

        public SwiftWindowControl()
        {
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            SetResourceReference(FontFamilyProperty, VsFonts.EnvironmentFontFamilyKey);
            SetResourceReference(FontSizeProperty, VsFonts.EnvironmentFontSizeKey);
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            Heading(header, "Swift");
            header.Children.Add(summary);
            root.Children.Add(header);
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(body, 1);
            root.Children.Add(body);
            navigation.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            navigation.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            body.Children.Add(navigation);
            Grid.SetColumn(actions, 1);
            body.Children.Add(actions);
            var toolchains = Page("Toolchain");
            toolchains.Children.Add(selection);
            toolchains.Children.Add(new TextBlock { Text = "Toolchain root or bin directory", Margin = new Thickness(0, 6, 0, 4) });
            toolchainPath.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            toolchainPath.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            toolchains.Children.Add(toolchainPath);
            var choose = Row(toolchains);
            Button(choose, "Browse...", BrowseAsync);
            Button(choose, "Validate", ValidateAsync);
            scope.Items.Add("Global"); scope.Items.Add("Workspace"); scope.SelectedIndex = 0;
            scope.Margin = new Thickness(0, 6, 6, 0);
            choose.Children.Add(scope);
            Button(choose, "Apply", ApplyAsync);
            Button(choose, "Use Global", UseGlobalAsync);
            toolchains.Children.Add(validation);
            var languageServer = Page("Language Server");
            languageServer.Children.Add(server);
            languageServer.Children.Add(active);
            languageServer.Children.Add(formatter);
            languageServer.Children.Add(new TextBlock { Text = "Signature Help: Ctrl+Shift+Space\nNavigation: standard Go To Implementation / Go To Type Definition commands", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) });
            Heading(languageServer, "Inlay Hints");
            foreach (var toggle in new[] { inlayEnabled, inlayTypes, inlayParameters })
            {
                toggle.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                languageServer.Children.Add(toggle);
            }
            inlayScope.Items.Add("Global"); inlayScope.Items.Add("Workspace"); inlayScope.SelectedIndex = 0;
            var hintsActions = Row(languageServer);
            hintsActions.Children.Add(inlayScope);
            Button(hintsActions, "Apply Hints", ApplyHintsAsync);
            Button(hintsActions, "Use Global Hints", UseGlobalHintsAsync);
            languageServer.Children.Add(new TextBlock { Text = "Applying hints restarts SourceKit-LSP to refresh open editors. Available hint kinds depend on the selected toolchain.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) });
            languageServer.Children.Add(new TextBlock { Text = "Format Document: Ctrl+K, Ctrl+D\nFormat Selection: Ctrl+K, Ctrl+F\nThe nearest .swift-format file takes priority over editor indentation settings.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
            var language = Row(languageServer);
            Button(language, "Restart", _ => SwiftPackage.RestartAsync());
            Button(language, "Show Output", _ => { navigation.SelectedIndex = 4; return Task.CompletedTask; });
            var packagePage = Page("Package");
            packagePage.Children.Add(package);
            packagePage.Children.Add(metadata);
            var packages = Row(packagePage);
            Button(packages, "Refresh Package", RefreshPackageAsync);
            Button(packages, "Open Package.swift", OpenManifestAsync);
            var debug = Page("Debug");
            stopOnEntry.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            useF5.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            debug.Children.Add(new TextBlock { Text = "Executable product", Margin = new Thickness(0, 8, 0, 4) });
            debug.Children.Add(debugProduct);
            debug.Children.Add(new TextBlock { Text = "Program arguments (JSON array)", Margin = new Thickness(0, 8, 0, 4) });
            debugArguments.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            debugArguments.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            debug.Children.Add(debugArguments);
            debug.Children.Add(stopOnEntry);
            debug.Children.Add(useF5);
            debug.Children.Add(new TextBlock { Text = "Builds Debug with LLVM/DWARF and opens the standard Visual Studio debugger. Configure breakpoints in the editor before launching.", TextWrapping = TextWrapping.Wrap });
            var debugging = Row(debug);
            Button(debugging, "Save", SaveDebugAsync);
            Button(debugging, "Start Debugging", StartDebugAsync);
            var outputPage = new Grid();
            outputPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outputPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outputPage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outputPage.Children.Add(new TextBlock { Text = "Output", FontSize = 16, FontWeight = FontWeights.SemiBold });
            var outputActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
            Button(outputActions, "Clear", _ => { output.Clear(); return Task.CompletedTask; });
            Grid.SetRow(outputActions, 1);
            outputPage.Children.Add(outputActions);
            output.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            output.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            Grid.SetRow(output, 2);
            outputPage.Children.Add(output);
            AddPage("Output", outputPage);
            navigation.SelectionChanged += (_, __) =>
            {
                for (var i = 0; i < actions.Children.Count; i++) actions.Children[i].Visibility = i == navigation.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
            };
            navigation.SelectedIndex = 0;
            var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(cancel, Dock.Right);
            footer.Children.Add(cancel);
            footer.Children.Add(message);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
            cancel.Click += (_, __) => operation?.Cancel();
            Loaded += OnLoaded;
            Unloaded += (_, __) => { SwiftRuntime.Changed -= RuntimeChanged; PrototypeLog.Written -= AppendOutput; operation?.Cancel(); };
        }

        private StackPanel Page(string title)
        {
            var content = new StackPanel { MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch };
            Heading(content, title);
            AddPage(title, new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            return content;
        }

        private void AddPage(string title, FrameworkElement page)
        {
            navigation.Items.Add(new ListBoxItem { Content = title, Padding = new Thickness(10, 8, 10, 8) });
            page.Visibility = Visibility.Collapsed;
            actions.Children.Add(page);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD001", Justification = "Output updates are dispatched to this control's UI thread without blocking the server.")]
        private void AppendOutput(string line)
        {
            if (Dispatcher.HasShutdownStarted) return;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                output.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine);
                if (output.Text.Length > 200000) output.Text = output.Text.Substring(output.Text.Length - 100000);
                output.ScrollToEnd();
            }));
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
            PrototypeLog.Written -= AppendOutput;
            PrototypeLog.Written += AppendOutput;
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
            if (output.Text.Length == 0 && File.Exists(PrototypeLog.FilePath))
            {
                var history = await Task.Run(() => File.ReadAllText(PrototypeLog.FilePath), token);
                output.Text = history.Length > 100000 ? history.Substring(history.Length - 100000) : history;
                output.ScrollToEnd();
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            SwiftRuntime.SetWorkspace(SwiftWorkspace.Current() ?? SwiftRuntime.Workspace);
            var workspace = SwiftRuntime.Workspace;
            var selected = await Task.Run(() => SwiftSettings.Default.Read(workspace), token);
            LoadHints();
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

        private void LoadHints()
        {
            hintsWorkspace = SwiftRuntime.Workspace;
            var hints = SwiftSettings.Default.ReadInlayHints(SwiftRuntime.Workspace);
            inlayEnabled.IsChecked = hints.Enabled;
            inlayTypes.IsChecked = hints.Types;
            inlayParameters.IsChecked = hints.Parameters;
            inlayScope.SelectedIndex = hints.Scope == "Workspace" ? 1 : 0;
        }

        private async Task ApplyHintsAsync(CancellationToken token)
        {
            var hints = new InlayHintSettings(inlayEnabled.IsChecked == true, inlayTypes.IsChecked == true, inlayParameters.IsChecked == true);
            SwiftSettings.Default.SaveInlayHints(hints, SwiftRuntime.Workspace, inlayScope.SelectedIndex == 1);
            if (SwiftRuntime.Restart != null) await SwiftPackage.RestartAsync();
        }

        private async Task UseGlobalHintsAsync(CancellationToken token)
        {
            SwiftSettings.Default.ClearWorkspaceInlayHints(SwiftRuntime.Workspace);
            LoadHints();
            if (SwiftRuntime.Restart != null) await SwiftPackage.RestartAsync();
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
            var saved = await Task.Run(() => SwiftDebugOptions.Read(workspace), token);
            debugProduct.Items.Clear();
            foreach (var product in (description["products"] as JArray ?? new JArray()).Where(p => p["type"]?["executable"] != null)) debugProduct.Items.Add(product.Value<string>("name"));
            if (debugProduct.Items.Contains(saved.Product)) debugProduct.SelectedItem = saved.Product;
            else if (debugProduct.Items.Count > 0) debugProduct.SelectedIndex = 0;
            debugArguments.Text = new JArray(saved.Arguments).ToString(Newtonsoft.Json.Formatting.None);
            stopOnEntry.IsChecked = saved.StopOnEntry;
            useF5.IsChecked = saved.UseF5;
        }

        private SwiftDebugOptions DebugOptions()
        {
            var arguments = JArray.Parse(debugArguments.Text);
            if (arguments.Any(value => value.Type != JTokenType.String)) throw new InvalidOperationException("Program arguments must be a JSON array of strings.");
            return new SwiftDebugOptions { Product = debugProduct.SelectedItem as string, Arguments = arguments.Values<string>().ToArray(), StopOnEntry = stopOnEntry.IsChecked == true, UseF5 = useF5.IsChecked == true };
        }

        private async Task SaveDebugAsync(CancellationToken token)
        {
            var workspace = SwiftRuntime.Workspace;
            var options = DebugOptions();
            if (options.Product == null) throw new InvalidOperationException("Select an executable SwiftPM product first.");
            await Task.Run(() => options.Save(workspace), token);
            PrototypeLog.Write("Saved Swift debug launch settings.");
        }

        private async Task StartDebugAsync(CancellationToken token)
        {
            var workspace = SwiftRuntime.Workspace;
            var options = DebugOptions();
            await SaveDebugAsync(token);
            await SwiftDebugLauncher.StartAsync(workspace, options, token);
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
            summary.Text = (SwiftRuntime.SelectedVersion?.Split('\n')[0].Trim() ?? "Toolchain not validated") + "  |  " + SwiftRuntime.State + "  |  " + (SwiftRuntime.Workspace == null ? "No package" : Path.GetFileName(SwiftRuntime.Workspace));
            try
            {
                var workspace = SwiftRuntime.Workspace;
                if (!string.Equals(hintsWorkspace, workspace, StringComparison.OrdinalIgnoreCase)) LoadHints();
                var selected = SwiftSettings.Default.Read(workspace);
                var bin = Toolchain.Resolve(workspace).BinDirectory;
                selection.Text = "Selected: " + bin + Environment.NewLine + "Source: " + selected.Scope;
                server.Text = "Status: " + SwiftRuntime.State + (SwiftRuntime.Error == null ? "" : Environment.NewLine + SwiftRuntime.Error);
                active.Text = "Active: " + (SwiftRuntime.ActiveBin ?? "None");
                var formatterBin = SwiftRuntime.ActiveBin ?? bin;
                var formatterPath = Path.Combine(formatterBin, "swift-format.exe");
                formatter.Text = File.Exists(formatterPath) ? "Formatter: " + formatterPath : "Formatter unavailable: swift-format.exe is missing from the toolchain.";
                if (SwiftRuntime.ActiveBin != null && (!string.Equals(bin, SwiftRuntime.ActiveBin, StringComparison.OrdinalIgnoreCase) || !string.Equals(workspace, SwiftRuntime.ActiveWorkspace, StringComparison.OrdinalIgnoreCase)))
                    active.Text += Environment.NewLine + "Restart required to use the selected toolchain/package.";
                package.Text = workspace == null ? "Package: None" : "Package: " + Path.Combine(workspace, "Package.swift");
            }
            catch (Exception error) { selection.Text = "Toolchain unavailable: " + error.Message; }
        }
    }
}
