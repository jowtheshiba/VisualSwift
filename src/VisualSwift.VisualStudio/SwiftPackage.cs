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
    [Guid("dbaf72a8-6a28-4316-a5c4-982085c40c5a")]
    public sealed class SwiftPackage : AsyncPackage
    {
        private static readonly Guid Commands = new Guid("5d796222-e1ce-44f3-9492-5475d8dac383");
        private static readonly Guid OutputId = new Guid("633cce02-6388-48d5-9092-dc5f948703aa");
        private IVsOutputWindowPane output;
        internal static SwiftPackage Instance { get; private set; }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Instance = this;
            var window = await GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow ?? throw new InvalidOperationException("Visual Studio Output is unavailable.");
            var id = OutputId;
            ErrorHandler.ThrowOnFailure(window.CreatePane(ref id, "Swift", 1, 1));
            ErrorHandler.ThrowOnFailure(window.GetPane(ref id, out output));
            PrototypeLog.Written += WriteOutput;
            WriteOutput("VisualSwift loaded. File log: " + PrototypeLog.FilePath);
            var menu = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Add(menu, 0x0101, async () => await ShowSwiftWindowAsync());
        }

        private OleMenuCommand Add(OleMenuCommandService menu, int id, Func<Task> action)
        {
            var command = new OleMenuCommand((_, __) => ExecuteCommand(action), new CommandID(Commands, id));
            menu.AddCommand(command);
            return command;
        }

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

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD010", Justification = "IVsOutputWindowPane.OutputStringThreadSafe explicitly supports background callers.")]
        private void WriteOutput(string message)
        {
            try { output?.OutputStringThreadSafe(DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine); }
            catch (COMException) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { PrototypeLog.Written -= WriteOutput; Instance = null; }
            base.Dispose(disposing);
        }
    }
}
