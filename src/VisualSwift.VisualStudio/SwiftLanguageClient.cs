using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace VisualSwift
{
    [Export(typeof(ILanguageClient)), ContentType("swift")]
    public sealed class SwiftLanguageClient : ILanguageClient, ILanguageClientCustomMessage2, IDisposable
    {
        private Process server;
        private SwiftProcessJob serverJob;
        private JsonRpc customRpc;
        private SwiftFileWatcher fileWatcher;
        private CancellationTokenSource fileRefresh;
        private readonly SemaphoreSlim restartGate = new SemaphoreSlim(1);
        private readonly SwiftMiddleLayer middleLayer = new SwiftMiddleLayer();

        public string Name => "VisualSwift SourceKit-LSP";
        public IEnumerable<string> ConfigurationSections => new[] { "swift" };
        public object InitializationOptions => new { backgroundIndexing = true };
        public IEnumerable<string> FilesToWatch => new[] { "**/Package.swift", "**/Package.resolved", "**/*.swift" };
        public bool ShowNotificationOnInitializeFailed => true;
        public object MiddleLayer => middleLayer;
        public object CustomMessageTarget => null;
        public event AsyncEventHandler<EventArgs> StartAsync;
        public event AsyncEventHandler<EventArgs> StopAsync;

        public async Task OnLoadedAsync()
        {
            SwiftRuntime.Restart = RestartServerAsync;
            PrototypeLog.Write("Language client loaded.");
            await SwiftPackage.EnsureLoadedAsync();
            if (StartAsync != null)
                await StartAsync.InvokeAsync(this, EventArgs.Empty);
        }

        public async Task<Connection> ActivateAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            DisposeServer();
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                var workspace = SwiftWorkspace.Current() ?? SwiftRuntime.Workspace;
                SwiftRuntime.SetWorkspace(workspace);
                var toolchain = await Task.Run(() => Toolchain.Resolve(workspace), token);
                middleLayer.Workspace = workspace;
                SwiftRuntime.SetServer("Starting", toolchain.BinDirectory, workspace);
                var startInfo = await Task.Run(() => toolchain.CreateServerStartInfo(workspace), token);
                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                var job = new SwiftProcessJob();
                server = process;
                serverJob = job;
                process.ErrorDataReceived += (_, args) => { if (args.Data != null) PrototypeLog.Write("server: " + args.Data); };
                process.Exited += (_, __) =>
                {
                    PrototypeLog.Write("SourceKit-LSP exited.");
                    if (ReferenceEquals(server, process)) SwiftRuntime.SetServer("Error", failure: "SourceKit-LSP exited. Open Swift Output for details.");
                };
                if (!process.Start())
                {
                    throw new InvalidOperationException("SourceKit-LSP could not be started.");
                }
                job.Attach(process);
                process.BeginErrorReadLine();
                PrototypeLog.Write("Started SourceKit-LSP pid=" + process.Id + " bin=" + toolchain.BinDirectory);
                var connection = new SwiftServerConnection(process, workspace);
                return connection;
            }
            catch (Exception error)
            {
                DisposeServer();
                SwiftRuntime.SetServer("Error", failure: error.Message);
                throw;
            }
        }

        public Task OnServerInitializedAsync()
        {
            PrototypeLog.Write("SourceKit-LSP initialized.");
            SwiftRuntime.SetServer("Running", SwiftRuntime.ActiveBin, SwiftRuntime.ActiveWorkspace);
            if (customRpc != null && middleLayer.Workspace != null)
            {
                var rpc = customRpc;
                fileWatcher = new SwiftFileWatcher(middleLayer.Workspace, changes => rpc.NotifyWithParameterObjectAsync("workspace/didChangeWatchedFiles", changes));
                fileWatcher.StructureChanged = QueueFileRefresh;
                middleLayer.FlushFileChanges = fileWatcher.FlushAsync;
            }
            return Task.CompletedTask;
        }

        public Task<InitializationFailureContext> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
        {
            PrototypeLog.Write("SourceKit-LSP initialization failed: " + initializationState.InitializationException);
            DisposeServer();
            SwiftRuntime.SetServer("Error", failure: initializationState.InitializationException?.Message ?? "SourceKit-LSP initialization failed.");
            return Task.FromResult(new InitializationFailureContext
            {
                FailureMessage = "SourceKit-LSP initialization failed. Check the selected Swift toolchain and the logs in %TEMP%\\VisualSwift."
            });
        }

        public Task AttachForCustomMessageAsync(JsonRpc rpc)
        {
            customRpc = rpc;
            return Task.CompletedTask;
        }

        public void Dispose() => DisposeServer();

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSSDK007", Justification = "The refresh task is tracked by FileAndForget, catches failures and is cancelled when the server is disposed.")]
        private void QueueFileRefresh()
        {
            var cancellation = new CancellationTokenSource();
            Interlocked.Exchange(ref fileRefresh, cancellation)?.Cancel();
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await Task.Delay(1000, cancellation.Token);
                    if (Interlocked.CompareExchange(ref fileRefresh, null, cancellation) != cancellation) return;
                    PrototypeLog.Write("Refreshing SourceKit-LSP after Swift source files were added, removed or renamed.");
                    await RestartServerAsync();
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { PrototypeLog.Write("Swift source file refresh failed: " + error.Message); }
                finally { cancellation.Dispose(); }
            }).FileAndForget("VisualSwift/SourceFileRefresh");
        }

        private async Task RestartServerAsync()
        {
            await restartGate.WaitAsync();
            try
            {
                if (StopAsync != null) await StopAsync.InvokeAsync(this, EventArgs.Empty);
                DisposeServer();
                if (StartAsync == null) throw new InvalidOperationException("Open a Swift file to activate SourceKit-LSP.");
                await StartAsync.InvokeAsync(this, EventArgs.Empty);
            }
            finally { restartGate.Release(); }
        }

        private void DisposeServer()
        {
            middleLayer.FlushFileChanges = null;
            Interlocked.Exchange(ref fileRefresh, null)?.Cancel();
            Interlocked.Exchange(ref fileWatcher, null)?.Dispose();
            customRpc = null;
            var process = Interlocked.Exchange(ref server, null);
            Interlocked.Exchange(ref serverJob, null)?.Dispose();
            SwiftRuntime.SetServer("Stopped");
            if (process == null) return;
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
    }

    internal sealed class SwiftServerConnection : Connection
    {
        private readonly Process process;

        public SwiftServerConnection(Process process, string workspace) : base(process.StandardOutput.BaseStream, new LspInputStream(process.StandardInput.BaseStream, workspace))
        {
            this.process = process;
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            if (!isDisposing) return;
            try
            {
                if (!process.WaitForExit(2000)) process.Kill();
                PrototypeLog.Write("SourceKit-LSP connection disposed.");
            }
            catch (InvalidOperationException) { }
        }
    }

    internal sealed class SwiftMiddleLayer : ILanguageClientMiddleLayer2<JToken>
    {
        public string Workspace { get; set; }
        public Func<Task> FlushFileChanges { get; set; }
        public bool CanHandle(string methodName) => true;

        public async Task HandleNotificationAsync(string methodName, JToken methodParam, Func<JToken, Task> sendNotification)
        {
            if (methodName == "textDocument/didOpen" && methodParam?["textDocument"] is JObject document)
                document["languageId"] = "swift";
            PrototypeLog.Write("notification " + methodName + " uri=" + (methodParam?["textDocument"]?["uri"] ?? methodParam?["uri"]));
            await sendNotification(methodParam);
        }

        public async Task<JToken> HandleRequestAsync(string methodName, JToken methodParam, Func<JToken, Task<JToken>> sendRequest)
        {
            if (methodName == "initialize" && Workspace != null && methodParam is JObject initialize)
            {
                var uri = new Uri(Workspace + Path.DirectorySeparatorChar).AbsoluteUri;
                initialize["rootUri"] = uri;
                initialize["rootPath"] = Workspace;
                initialize["workspaceFolders"] = new JArray(new JObject { ["uri"] = uri, ["name"] = Path.GetFileName(Workspace) });
            }
            PrototypeLog.Write("request " + methodName);
            var hints = methodName == "textDocument/inlayHint" ? SwiftSettings.Default.ReadInlayHints(Workspace) : null;
            if (hints != null && !hints.Enabled) return new JArray();
            if (methodName.StartsWith("textDocument/", StringComparison.Ordinal) && FlushFileChanges != null)
                await FlushFileChanges();
            JToken result;
            try { result = await sendRequest(methodParam); }
            catch (Exception error) when (methodName == "textDocument/formatting" || methodName == "textDocument/rangeFormatting")
            {
                PrototypeLog.Write("Swift formatting failed: " + error.Message);
                throw;
            }
            if (hints != null && result is JArray inlayHints) result = hints.Filter(inlayHints);
            PrototypeLog.Write("response " + methodName + " " + result?.ToString(Newtonsoft.Json.Formatting.None));
            return result;
        }
    }
}
