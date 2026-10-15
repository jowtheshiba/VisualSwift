using System;
using System.Runtime.InteropServices;
using System.Threading;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace VisualSwift
{
    internal sealed class SwiftDebugCommands : IOleCommandTarget
    {
        private readonly SwiftPackage package;
        public SwiftDebugCommands(SwiftPackage package) { this.package = package; }
        private bool CanHandle(ref Guid group, uint id, out string workspace, out SwiftDebugOptions options)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            workspace = null; options = null;
            if (group != VSConstants.GUID_VSStandardCommandSet97 || id != (uint)VSConstants.VSStd97CmdID.Start) return false;
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode) return false;
            try
            {
                var startup = dte.Solution.IsOpen ? dte.Solution.SolutionBuild.StartupProjects as Array : null;
                if (startup?.Length == 1 && startup.GetValue(0) is string path)
                    workspace = SwiftWorkspace.FindPackage(System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dte.Solution.FullName), path)));
                else if (dte.Solution.IsOpen) return false;
                else workspace = SwiftWorkspace.Current();
                if (workspace == null) return false;
                options = SwiftDebugOptions.Read(workspace, SwiftBuildConfigurationEvents.CurrentConfiguration(dte, workspace));
                return options.UseF5 && !string.IsNullOrWhiteSpace(options.Product);
            }
            catch (Exception) { return false; }
        }
        public int QueryStatus(ref Guid group, uint count, OLECMD[] commands, IntPtr text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var handled = false;
            for (var index = 0; index < count; index++)
            {
                if (!CanHandle(ref group, commands[index].cmdID, out _, out _)) continue;
                commands[index].cmdf = (uint)OLECMDF.OLECMDF_SUPPORTED | (SwiftDebugLauncher.IsPreparing ? 0 : (uint)OLECMDF.OLECMDF_ENABLED);
                handled = true;
            }
            return handled ? VSConstants.S_OK : (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSSDK007", Justification = "The synchronous OLE callback schedules a launch with the package disposal token; failures are caught and the task is tracked by FileAndForget.")]
        public int Exec(ref Guid group, uint id, uint options, IntPtr input, IntPtr output)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!CanHandle(ref group, id, out var workspace, out var launch)) return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try { await SwiftDebugLauncher.StartAsync(workspace, launch, package.DisposalTokenForDebug); }
                catch (OperationCanceledException) { PrototypeLog.Write("Swift debug launch cancelled."); }
                catch (Exception error)
                {
                    PrototypeLog.Write("Swift debug launch failed: " + error);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    VsShellUtilities.ShowMessageBox(package, error.Message, "Swift Debug", Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_WARNING, Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_OK, Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                }
            }).FileAndForget("VisualSwift/DebugLaunch");
            return VSConstants.S_OK;
        }
    }
}
