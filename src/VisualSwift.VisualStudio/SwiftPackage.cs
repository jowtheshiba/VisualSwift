using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VisualSwift
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(SwiftToolWindow), Width = 850, Height = 600)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid("dbaf72a8-6a28-4316-a5c4-982085c40c5a")]
    public sealed class SwiftPackage : AsyncPackage
    {
        private static readonly Guid Commands = new Guid("5d796222-e1ce-44f3-9492-5475d8dac383");
        private static readonly Guid OutputId = new Guid("633cce02-6388-48d5-9092-dc5f948703aa");
        private IVsOutputWindowPane output;
        private IVsRegisterPriorityCommandTarget debugCommands;
        private uint debugCommandCookie;
        private EnvDTE.SolutionEvents solutionEvents;
        private EnvDTE.WindowEvents windowEvents;
        private SwiftDependencyExplorer dependencyExplorer;
        private SwiftBuildConfigurationEvents buildConfigurations;
        internal CancellationToken DisposalTokenForDebug => DisposalToken;
        internal static SwiftPackage Instance { get; private set; }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Instance = this;
            var dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            if (dte != null)
            {
                solutionEvents = dte.Events.SolutionEvents;
                windowEvents = dte.Events.WindowEvents;
                solutionEvents.Opened += UpdateWorkspace;
                solutionEvents.AfterClosing += UpdateWorkspace;
                solutionEvents.ProjectAdded += ProjectChanged;
                solutionEvents.ProjectRemoved += ProjectChanged;
                solutionEvents.ProjectRenamed += ProjectRenamed;
                windowEvents.WindowActivated += WindowActivated;
            }
            UpdateWorkspace();
            var window = await GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow ?? throw new InvalidOperationException("Visual Studio Output is unavailable.");
            var id = OutputId;
            ErrorHandler.ThrowOnFailure(window.CreatePane(ref id, "Swift", 1, 1));
            ErrorHandler.ThrowOnFailure(window.GetPane(ref id, out output));
            PrototypeLog.Written += WriteOutput;
            WriteOutput("VisualSwift loaded. File log: " + PrototypeLog.FilePath);
            var menu = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Add(menu, 0x0101, async () => await ShowSwiftWindowAsync());
            foreach (var commandId in new[] { 0x0301, 0x0302 })
            {
                var types = commandId == 0x0302;
                var command = Add(menu, commandId, () => ShowHierarchyAsync(types));
                command.BeforeQueryStatus += (_, __) =>
                {
                    var available = dte?.ActiveDocument?.FullName?.EndsWith(".swift", StringComparison.OrdinalIgnoreCase) == true;
                    command.Visible = available;
                    command.Enabled = available && SwiftRuntime.State == "Running";
                };
            }
            if (dte != null && menu != null) dependencyExplorer = new SwiftDependencyExplorer(dte, menu, DisposalToken);
            if (dte != null) buildConfigurations = new SwiftBuildConfigurationEvents(dte, DisposalToken);
            debugCommands = await GetServiceAsync(typeof(SVsRegisterPriorityCommandTarget)) as IVsRegisterPriorityCommandTarget;
            if (debugCommands != null) ErrorHandler.ThrowOnFailure(debugCommands.RegisterPriorityCommandTarget(0, new SwiftDebugCommands(this), out debugCommandCookie));
            PrototypeLog.Write("Swift package initialized; debug command target=" + debugCommandCookie);
        }

        internal static async Task EnsureLoadedAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (Instance?.debugCommandCookie > 0) return;
            var shell = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsShell)) as IVsShell
                ?? throw new InvalidOperationException("Visual Studio package loading is unavailable.");
            var id = typeof(SwiftPackage).GUID;
            ErrorHandler.ThrowOnFailure(shell.LoadPackage(ref id, out _));
        }

        private OleMenuCommand Add(OleMenuCommandService menu, int id, Func<Task> action)
        {
            var command = new OleMenuCommand((_, __) => ExecuteCommand(action), new CommandID(Commands, id));
            menu.AddCommand(command);
            return command;
        }

        private void WindowActivated(EnvDTE.Window current, EnvDTE.Window previous)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            UpdateWorkspace();
        }

        private void UpdateWorkspace()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var current = SwiftWorkspace.Current();
                var roots = SwiftWorkspace.Projects(dte);
                SwiftRuntime.SetProjects(dte?.Solution.IsOpen == true || current == null ? roots : new[] { current });
                SwiftRuntime.SetWorkspace(current);
            }
            catch (Exception error) { PrototypeLog.Write("Swift workspace update: " + error.Message); }
        }
        private void ProjectChanged(EnvDTE.Project project) { ThreadHelper.ThrowIfNotOnUIThread(); UpdateWorkspace(); }
        private void ProjectRenamed(EnvDTE.Project project, string previous) { ThreadHelper.ThrowIfNotOnUIThread(); UpdateWorkspace(); }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100", Justification = "The menu callback returns void; asynchronous operation errors are handled here.")]
        private async void ExecuteCommand(Func<Task> action)
        {
            try { await action(); }
            catch (Exception error)
            {
                PrototypeLog.Write(error.ToString());
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                VsShellUtilities.ShowMessageBox(this, error.Message, "Swift", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }

        internal async Task ShowSwiftWindowAsync()
        {
            var window = await ShowToolWindowAsync(typeof(SwiftToolWindow), 0, true, DisposalToken);
            if (window == null) throw new InvalidOperationException("The Swift window could not be created.");
        }

        internal static async Task RestartAsync()
        {
            var restart = SwiftRuntime.Restart;
            if (restart == null) throw new InvalidOperationException("Open a Swift file to activate the language server first.");
            await restart();
        }

        private async Task ShowHierarchyAsync(bool types)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            var dte = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            var document = dte?.ActiveDocument;
            if (!(document?.Selection is EnvDTE.TextSelection selected)) return;
            var uri = new Uri(document.FullName).AbsoluteUri;
            var line = selected.ActivePoint.Line - 1;
            var character = selected.ActivePoint.LineCharOffset - 1;
            var window = await ShowToolWindowAsync(typeof(SwiftToolWindow), 0, true, DisposalToken);
            await ((SwiftWindowControl)window.Content).ShowHierarchyAsync(uri, line, character, types);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD010", Justification = "IVsOutputWindowPane.OutputStringThreadSafe explicitly supports background callers.")]
        private void WriteOutput(string message)
        {
            try { output?.OutputStringThreadSafe(DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine); }
            catch (COMException) { }
        }

        protected override void Dispose(bool disposing)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposing)
            {
                if (solutionEvents != null) { solutionEvents.Opened -= UpdateWorkspace; solutionEvents.AfterClosing -= UpdateWorkspace; solutionEvents.ProjectAdded -= ProjectChanged; solutionEvents.ProjectRemoved -= ProjectChanged; solutionEvents.ProjectRenamed -= ProjectRenamed; }
                if (windowEvents != null) windowEvents.WindowActivated -= WindowActivated;
                PrototypeLog.Written -= WriteOutput;
                dependencyExplorer?.Dispose();
                buildConfigurations?.Dispose();
                if (debugCommandCookie != 0) debugCommands?.UnregisterPriorityCommandTarget(debugCommandCookie);
                Instance = null;
            }
            base.Dispose(disposing);
        }
    }
}
