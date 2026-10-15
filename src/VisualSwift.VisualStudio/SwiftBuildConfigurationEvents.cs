using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VisualSwift
{
    internal sealed class SwiftBuildConfigurationEvents : IVsUpdateSolutionEvents, IDisposable
    {
        private readonly DTE dte;
        private readonly IVsSolutionBuildManager manager;
        private readonly CancellationToken disposal;
        private uint cookie;
        private string previous;
        private CancellationTokenSource refresh;
        public SwiftBuildConfigurationEvents(DTE dte, CancellationToken disposal)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.dte = dte; this.disposal = disposal;
            previous = ConfigurationKey();
            manager = Package.GetGlobalService(typeof(SVsSolutionBuildManager)) as IVsSolutionBuildManager;
            if (manager != null) ErrorHandler.ThrowOnFailure(manager.AdviseUpdateSolutionEvents(this, out cookie));
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSSDK007", Justification = "The configuration event task catches failures and is tracked by FileAndForget.")]
        public int OnActiveProjectCfgChange(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var configuration = ConfigurationKey();
            if (configuration == previous) return VSConstants.S_OK;
            previous = configuration;
            refresh?.Cancel();
            if (SwiftRuntime.Workspaces.Length == 0 || SwiftRuntime.Restart == null) return VSConstants.S_OK;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(disposal);
            refresh = cancellation;
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await Task.Delay(500, cancellation.Token);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation.Token);
                    if (SwiftRuntime.Restart != null) await SwiftRuntime.Restart();
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { PrototypeLog.Write("Swift configuration refresh: " + error.Message); }
                finally { if (refresh == cancellation) refresh = null; cancellation.Dispose(); }
            }).FileAndForget("VisualSwift/BuildConfiguration");
            return VSConstants.S_OK;
        }
        public int UpdateSolution_Begin(ref int cancel) => VSConstants.S_OK;
        public int UpdateSolution_Done(int succeeded, int modified, int cancel) => VSConstants.S_OK;
        public int UpdateSolution_StartUpdate(ref int cancel) => VSConstants.S_OK;
        public int UpdateSolution_Cancel() => VSConstants.S_OK;
        private string ConfigurationKey() { ThreadHelper.ThrowIfNotOnUIThread(); return string.Join("|", SwiftRuntime.Workspaces.Select(root => root + "=" + CurrentConfiguration(dte, root))); }
        internal static string CurrentConfiguration(DTE dte, string workspace = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var active = dte?.Solution.IsOpen == true ? dte.Solution.SolutionBuild.ActiveConfiguration : null;
                if (workspace != null && active != null)
                    foreach (SolutionContext context in active.SolutionContexts)
                    {
                        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dte.Solution.FullName), context.ProjectName));
                        if (string.Equals(SwiftWorkspace.FindPackage(path), workspace, StringComparison.OrdinalIgnoreCase)) return context.ConfigurationName;
                    }
                return active?.Name ?? "Debug";
            }
            catch (System.Runtime.InteropServices.COMException) { return "Debug"; }
        }
        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            refresh?.Cancel();
            if (cookie != 0) manager?.UnadviseUpdateSolutionEvents(cookie);
        }
    }
}
