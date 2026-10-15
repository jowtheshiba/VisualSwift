using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using EnvDTE;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.VCProjectEngine;
using Newtonsoft.Json.Linq;

namespace VisualSwift
{
    internal sealed class SwiftProjectSettingsWindow : DialogWindow
    {
        private readonly Project project;
        private readonly string workspace;
        private readonly SwiftProjectSettings settings;
        private readonly ComboBox configuration = new ComboBox { ItemsSource = new[] { "Debug", "Release" }, SelectedIndex = 0, MinWidth = 160 };
        private readonly ComboBox optimization = new ComboBox { ItemsSource = new[] { "Default", "-Onone", "-O", "-Osize" } };
        private readonly ComboBox interoperability = new ComboBox { ItemsSource = new[] { "From Package.swift", "Enabled for all Swift targets" }, SelectedIndex = 0 };
        private readonly ComboBox product = new ComboBox { IsEditable = true };
        private readonly Dictionary<string, TextBox> fields = new Dictionary<string, TextBox>();
        private readonly CheckBox build = new CheckBox { Content = "Build before launch (F5)" };
        private readonly CheckBox f5 = new CheckBox { Content = "Enable Swift debugging through F5" };
        private readonly CheckBox entry = new CheckBox { Content = "Stop on entry" };
        private readonly TextBox toolchain = new TextBox();
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private string editing = "Debug";
        private bool changing;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSSDK007", Justification = "Window tasks catch failures and are tracked by FileAndForget.")]
        public SwiftProjectSettingsWindow(Project project, string workspace)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.project = project;
            this.workspace = workspace;
            settings = SwiftProjectSettings.Read(workspace);
            Title = project.Name + " — Swift Project Settings";
            Width = 880; Height = 700;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            new System.Windows.Interop.WindowInteropHelper(this).Owner = ((DTE)Package.GetGlobalService(typeof(DTE))).MainWindow.HWnd;
            var panel = new DockPanel { Margin = new Thickness(16) };
            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            heading.Children.Add(new TextBlock { Text = workspace, TextWrapping = TextWrapping.Wrap });
            heading.Children.Add(new TextBlock { Text = "Configuration", Margin = new Thickness(0, 10, 0, 4) });
            heading.Children.Add(configuration);
            DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
            var bottom = new StackPanel();
            bottom.Children.Add(status); bottom.Children.Add(buttons);
            DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
            foreach (var label in new[] { "Apply", "OK", "Cancel" })
            {
                var button = new Button { Content = label, Margin = new Thickness(8), Padding = new Thickness(16, 6, 16, 6), IsCancel = label == "Cancel" };
                if (label != "Cancel") button.Click += (_, __) => ThreadHelper.JoinableTaskFactory.RunAsync(() => ApplyAsync(label == "OK")).FileAndForget("VisualSwift/ProjectSettings");
                buttons.Children.Add(button);
            }
            var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) }); body.ColumnDefinitions.Add(new ColumnDefinition());
            var navigation = new ListBox { ItemsSource = new[] { "Build", "Run & Debug", "Toolchain" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 16, 0) };
            body.Children.Add(navigation);
            var pages = new Grid(); Grid.SetColumn(pages, 1); body.Children.Add(pages);
            var buildPage = Page(pages);
            Field(buildPage, "Optimization (Default uses SwiftPM defaults)", optimization);
            Field(buildPage, "C++ interoperability", interoperability);
            buildPage.Children.Add(new TextBlock { Text = "Package.swift defines target dependencies, public headers and the C++ standard. Enabling this override also applies to Swift test targets.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) });
            TextField(buildPage, "Conditions", "Compilation conditions — one identifier per line", 60);
            TextField(buildPage, "SwiftFlags", "Swift flags — one argument per line", 75);
            TextField(buildPage, "CFlags", "C flags — one argument per line", 50);
            TextField(buildPage, "CxxFlags", "C++ flags — one argument per line", 50);
            TextField(buildPage, "LinkerFlags", "Linker flags — one argument per line", 50);
            var runPage = Page(pages);
            Field(runPage, "Executable product", product);
            TextField(runPage, "Arguments", "Launch arguments — one argument per line", 75);
            TextField(runPage, "Environment", "Environment variables — NAME=value, one per line", 100);
            TextField(runPage, "WorkingDirectory", "Working directory — empty uses the package root", 30);
            foreach (var checkbox in new[] { build, f5, entry }) { checkbox.Margin = new Thickness(0, 10, 0, 0); runPage.Children.Add(checkbox); }
            var toolchainPage = Page(pages);
            Field(toolchainPage, "Project toolchain path — empty inherits the default toolchain", toolchain);
            var selection = SwiftSettings.Default.Read(workspace);
            toolchain.Text = selection.Scope == "Workspace" ? selection.Path : "";
            toolchainPage.Children.Add(new TextBlock { Text = "Current: " + (selection.Path ?? "PATH") + " (" + selection.Scope + ")\nApplies to both configurations. The active language server refreshes after Apply.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
            navigation.SelectionChanged += (_, __) => { for (var index = 0; index < pages.Children.Count; index++) pages.Children[index].Visibility = index == navigation.SelectedIndex ? Visibility.Visible : Visibility.Collapsed; };
            pages.Children[1].Visibility = Visibility.Collapsed; pages.Children[2].Visibility = Visibility.Collapsed;
            panel.Children.Add(body); Content = panel;
            SwiftPackageTheme.Apply(this);
            editing = ((DTE)Package.GetGlobalService(typeof(DTE))).Solution.SolutionBuild.ActiveConfiguration.Name == "Release" ? "Release" : "Debug";
            configuration.SelectedItem = editing;
            LoadProfile();
            configuration.SelectionChanged += (_, __) =>
            {
                if (changing) return;
                try { Capture(); editing = (string)configuration.SelectedItem; LoadProfile(); status.Text = ""; }
                catch (Exception error) { status.Text = error.Message; changing = true; configuration.SelectedItem = editing; changing = false; }
            };
            Closed += (_, __) => cancellation.Cancel();
            ThreadHelper.JoinableTaskFactory.RunAsync(LoadProductsAsync).FileAndForget("VisualSwift/ProjectProducts");
        }
        private static StackPanel Page(Grid pages)
        {
            var panel = new StackPanel { Margin = new Thickness(2) };
            pages.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            return panel;
        }
        private static void Field(StackPanel panel, string label, FrameworkElement control)
        {
            panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) }); panel.Children.Add(control);
        }
        private void TextField(StackPanel panel, string key, string label, double height)
        {
            var field = new TextBox { AcceptsReturn = height > 30, Height = height, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            fields[key] = field; Field(panel, label, field);
        }
        private static string[] Lines(string value) => value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        private void LoadProfile()
        {
            var profile = settings.Profile(editing);
            optimization.SelectedItem = profile.Optimization;
            interoperability.SelectedIndex = profile.CxxInteroperability ? 1 : 0;
            foreach (var key in new[] { "Conditions", "SwiftFlags", "CFlags", "CxxFlags", "LinkerFlags", "Arguments" }) fields[key].Text = string.Join(Environment.NewLine, (string[])typeof(SwiftProjectProfile).GetProperty(key).GetValue(profile));
            fields["Environment"].Text = string.Join(Environment.NewLine, profile.Environment.Select(pair => pair.Key + "=" + pair.Value));
            fields["WorkingDirectory"].Text = profile.WorkingDirectory ?? "";
            product.Text = profile.Product ?? "";
            build.IsChecked = profile.BuildBeforeLaunch; f5.IsChecked = profile.UseF5; entry.IsChecked = profile.StopOnEntry;
        }
        private void Capture()
        {
            var profile = new SwiftProjectProfile { Optimization = (string)optimization.SelectedItem, Product = product.Text.Trim(), WorkingDirectory = fields["WorkingDirectory"].Text.Trim(), BuildBeforeLaunch = build.IsChecked == true, UseF5 = f5.IsChecked == true, StopOnEntry = entry.IsChecked == true };
            profile.CxxInteroperability = interoperability.SelectedIndex == 1;
            foreach (var key in new[] { "Conditions", "SwiftFlags", "CFlags", "CxxFlags", "LinkerFlags", "Arguments" }) typeof(SwiftProjectProfile).GetProperty(key).SetValue(profile, Lines(fields[key].Text));
            foreach (var line in Lines(fields["Environment"].Text))
            {
                var separator = line.IndexOf('=');
                if (separator < 1) throw new InvalidOperationException("Enter environment variables as NAME=value.");
                var name = line.Substring(0, separator).Trim();
                if (profile.Environment.ContainsKey(name)) throw new InvalidOperationException("Duplicate environment variable: " + name);
                profile.Environment.Add(name, line.Substring(separator + 1));
            }
            profile.Validate(); profile.ResolveWorkingDirectory(workspace);
            if (editing == "Release") settings.Release = profile; else settings.Debug = profile;
        }
        private async Task LoadProductsAsync()
        {
            try
            {
                var compiler = await Task.Run(() => Toolchain.Resolve(workspace), cancellation.Token);
                var manifest = JObject.Parse(await SwiftProcess.RunAsync(compiler, "package dump-package", workspace, cancellation.Token));
                var names = (manifest["products"] as JArray ?? new JArray()).Where(item => item["type"]?["executable"] != null).Select(item => (string)item["name"]).ToArray();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation.Token);
                var selected = product.Text; product.ItemsSource = names; product.Text = selected;
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { status.Text = error.Message; }
        }
        private async Task ApplyAsync(bool close)
        {
            buttons.IsEnabled = false;
            try
            {
                Capture(); settings.Debug.Validate(); settings.Release.Validate();
                settings.Debug.ResolveWorkingDirectory(workspace); settings.Release.ResolveWorkingDirectory(workspace);
                var path = toolchain.Text.Trim();
                if (path.Length > 0) await Toolchain.FromPath(path).ValidateAsync(cancellation.Token);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation.Token);
                var runtime = Path.Combine(Path.GetDirectoryName(typeof(SwiftPackage).Assembly.Location), "Runtime");
                var helper = Path.Combine(workspace, "VisualSwift"); Directory.CreateDirectory(helper);
                foreach (var name in new[] { "Invoke-Swift.ps1", "WindowsSwiftEnvironment.ps1" }) File.Copy(Path.Combine(runtime, name), Path.Combine(helper, name), true);
                settings.Save(workspace);
                if (path.Length == 0) SwiftSettings.Default.ClearWorkspace(workspace); else SwiftSettings.Default.Save(path, workspace, true);
                ApplyNativeLaunch(); project.Save();
                status.Text = "Saved Debug and Release profiles for " + project.Name + ".";
                if (SwiftRuntime.Restart != null) await SwiftRuntime.Restart();
                if (close) Close();
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { status.Text = error.Message; PrototypeLog.Write("Swift Project Settings: " + error); }
            finally { buttons.IsEnabled = true; }
        }
        private void ApplyNativeLaunch()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(project.Object is VCProject native)) return;
            foreach (VCConfiguration configuration in (IVCCollection)native.Configurations)
            {
                var profile = settings.Profile(configuration.ConfigurationName);
                if (string.IsNullOrWhiteSpace(profile.Product)) continue;
                var executable = Path.Combine(workspace, ".build", configuration.ConfigurationName.Equals("Release", StringComparison.OrdinalIgnoreCase) ? "release" : "debug", profile.Product + ".exe");
                foreach (var tool in (IVCCollection)configuration.Tools) if (tool is VCNMakeTool make) make.Output = executable;
                var debug = (VCDebugSettings)configuration.DebugSettings;
                debug.Command = executable; debug.CommandArguments = string.Join(" ", profile.Arguments.Select(SwiftPackages.Quote));
                debug.WorkingDirectory = profile.ResolveWorkingDirectory(workspace);
                debug.Environment = string.Join(Environment.NewLine, profile.Environment.Select(pair => pair.Key + "=" + pair.Value)); debug.EnvironmentMerge = true;
            }
        }
    }
}
