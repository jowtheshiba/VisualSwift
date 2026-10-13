using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TemplateWizard;

namespace VisualSwift
{
    public sealed class SwiftItemTemplateWizard : IWizard
    {
        private DTE dte;
        private Project project;
        private string destination;

        public void RunStarted(object automationObject, Dictionary<string, string> replacementsDictionary, WizardRunKind runKind, object[] customParams)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            PrototypeLog.Write("Swift item wizard started.");
            dte = (DTE)automationObject;
            var selection = dte.SelectedItems.Count == 1 ? dte.SelectedItems.Item(1) : null;
            project = selection?.Project ?? selection?.ProjectItem?.ContainingProject;
            if (project == null && dte.ActiveSolutionProjects is Array projects && projects.Length == 1)
                project = projects.GetValue(0) as Project;
            var root = SwiftWorkspace.FindPackage(project?.FullName);
            if (root == null) throw new InvalidOperationException("Select a SwiftPM project containing Package.swift before adding a Swift file.");
            var directories = new[] { "Sources", "Tests" }.Select(name => Path.Combine(root, name)).Where(Directory.Exists).SelectMany(Directory.GetDirectories).ToArray();
            if (directories.Length == 0) throw new InvalidOperationException("The package has no Sources or Tests target folder. Create a SwiftPM target first.");
            var selectedPath = selection?.ProjectItem == null ? null : ItemPath(selection.ProjectItem);
            var selectedDirectory = selectedPath == null ? null : Directory.Exists(selectedPath) ? selectedPath : Path.GetDirectoryName(selectedPath);
            var directory = directories.FirstOrDefault(path => selectedDirectory != null && (string.Equals(path, selectedDirectory, StringComparison.OrdinalIgnoreCase) || selectedDirectory.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
            if (directory != null) directory = selectedDirectory;
            else directory = directories.Length == 1 ? directories[0] : SelectTarget(root, directories);
            var name = replacementsDictionary.TryGetValue("$rootname$", out var rootName) ? rootName : replacementsDictionary["$fileinputname$"];
            if (!name.EndsWith(".swift", StringComparison.OrdinalIgnoreCase)) name += ".swift";
            if (Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("Enter a Swift filename without a directory path.");
            destination = Path.Combine(directory, name);
            if (File.Exists(destination)) throw new InvalidOperationException("A file with this name already exists in the selected target: " + destination);
            CreateItem();
        }

        private static string ItemPath(ProjectItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return item.FileCount > 0 ? item.FileNames[1] : null; }
            catch (System.Runtime.InteropServices.COMException) { return null; }
        }

        private static string SelectTarget(string root, string[] directories)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var window = new System.Windows.Window { Title = "Swift File", Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            new System.Windows.Interop.WindowInteropHelper(window).Owner = ((DTE)Package.GetGlobalService(typeof(DTE))).MainWindow.HWnd;
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "Choose the SwiftPM target for this file:", Margin = new Thickness(0, 0, 0, 12) });
            var targets = new ComboBox { ItemsSource = directories.Select(path => path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1)).ToArray(), SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(targets);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var create = new Button { Content = "Add", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
            create.Click += (_, __) => window.DialogResult = true;
            buttons.Children.Add(create);
            buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Width = 80 });
            panel.Children.Add(buttons);
            window.Content = panel;
            if (window.ShowDialog() != true) throw new WizardBackoutException();
            return directories[targets.SelectedIndex];
        }

        private void CreateItem()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.WriteLine();
            var root = Path.GetDirectoryName(project.FullName);
            var relativeDirectory = Path.GetDirectoryName(destination).Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
            var items = project.ProjectItems;
            foreach (var folder in relativeDirectory.Split(Path.DirectorySeparatorChar))
            {
                var group = FindGroup(items, folder);
                if (group == null) group = items.AddFolder(folder);
                items = group.ProjectItems;
            }
            var item = items.AddFromFile(destination);
            item.Open().Activate();
            PrototypeLog.Write("Swift item added: " + destination);
        }

        public void RunFinished() { }
        public bool ShouldAddProjectItem(string filePath) => false;
        private static ProjectItem FindGroup(ProjectItems items, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (ProjectItem item in items)
                if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) return item;
            return null;
        }
        public void BeforeOpeningFile(ProjectItem projectItem) { }
        public void ProjectFinishedGenerating(Project generatedProject) { }
        public void ProjectItemFinishedGenerating(ProjectItem projectItem) { }
    }
}
