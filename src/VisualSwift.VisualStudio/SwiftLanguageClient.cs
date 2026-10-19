using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private SwiftLspBroker broker;
        private JsonRpc customRpc;
        private readonly List<SwiftFileWatcher> fileWatchers = new List<SwiftFileWatcher>();
        private string[] serverRoots = Array.Empty<string>();
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
            SwiftRuntime.ProjectsChanged += ProjectsChanged;
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
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                SwiftRuntime.SetWorkspace(workspace);
                serverRoots = SwiftRuntime.Workspaces.Concat(new[] { workspace }).Where(root => root != null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var configurations = serverRoots.ToDictionary(root => root, root => SwiftBuildConfigurationEvents.CurrentConfiguration(dte, root), StringComparer.OrdinalIgnoreCase);
                middleLayer.Workspace = workspace;
                var bins = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                broker = new SwiftLspBroker(serverRoots, async (root, cancellation) =>
                {
                    var toolchain = await Task.Run(() => Toolchain.Resolve(root), cancellation).ConfigureAwait(false);
                    bins[root] = toolchain.BinDirectory;
                    var startInfo = await Task.Run(() => toolchain.CreateServerStartInfo(root), cancellation).ConfigureAwait(false);
                    var configuration = configurations[root];
                    if (SwiftProjectSettings.Exists(root)) startInfo.Arguments = SwiftProjectSettings.Read(root).Profile(configuration).BuildArguments(configuration, "--configuration");
                    var process = new Process { StartInfo = startInfo };
                    var job = new SwiftProcessJob();
                    try
                    {
                        process.ErrorDataReceived += (_, args) => { if (args.Data != null) PrototypeLog.Write("server [" + root + "]: " + args.Data); };
                        if (!process.Start()) throw new InvalidOperationException("SourceKit-LSP could not be started.");
                        job.Attach(process); process.BeginErrorReadLine();
                        PrototypeLog.Write("Started SourceKit-LSP pid=" + process.Id + " package=" + root + " bin=" + toolchain.BinDirectory);
                        return new SwiftLspEndpoint(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, () =>
                        {
                            job.Dispose();
                            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                            process.Dispose();
                        });
                    }
                    catch { job.Dispose(); process.Dispose(); throw; }
                }, (root, state, failure) => { bins.TryGetValue(root, out var bin); SwiftRuntime.SetServer(state, bin, root, failure); });
                return new SwiftBrokerConnection(broker);
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
            if (customRpc != null)
            {
                var rpc = customRpc;
                foreach (var root in serverRoots)
                {
                    var watcher = new SwiftFileWatcher(root, changes => rpc.NotifyWithParameterObjectAsync("workspace/didChangeWatchedFiles", changes));
                    watcher.StructureChanged = QueueFileRefresh; fileWatchers.Add(watcher);
                }
                middleLayer.FlushFileChanges = () => Task.WhenAll(fileWatchers.Select(watcher => watcher.FlushAsync()).ToArray());
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
            SwiftRuntime.Request = (method, parameters) => rpc.InvokeWithParameterObjectAsync<JToken>(method, parameters);
            middleLayer.CodeActions.ResolveAsync = action => rpc.InvokeWithParameterObjectAsync<JToken>("codeAction/resolve", action);
            return Task.CompletedTask;
        }

        public void Dispose() { SwiftRuntime.ProjectsChanged -= ProjectsChanged; DisposeServer(); }
        private void ProjectsChanged() { if (broker != null && !serverRoots.OrderBy(root => root, StringComparer.OrdinalIgnoreCase).SequenceEqual(SwiftRuntime.Workspaces.OrderBy(root => root, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) QueueFileRefresh(); }

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
            IDisposable editors = null;
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                editors = SwiftEditorSessions.Suspend();
                if (StopAsync != null) await StopAsync.InvokeAsync(this, EventArgs.Empty);
                DisposeServer();
                if (StartAsync == null) throw new InvalidOperationException("Open a Swift file to activate SourceKit-LSP.");
                await StartAsync.InvokeAsync(this, EventArgs.Empty);
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try { editors?.Dispose(); }
                finally { restartGate.Release(); }
            }
        }

        private void DisposeServer()
        {
            middleLayer.FlushFileChanges = null;
            middleLayer.Dispose();
            Interlocked.Exchange(ref fileRefresh, null)?.Cancel();
            foreach (var watcher in fileWatchers) watcher.Dispose();
            fileWatchers.Clear();
            customRpc = null;
            SwiftRuntime.Request = null;
            middleLayer.CodeActions.Clear();
            Interlocked.Exchange(ref broker, null)?.Dispose();
            SwiftRuntime.SetServer("Stopped");
        }
    }

    internal sealed class SwiftBrokerConnection : Connection
    {
        private readonly SwiftLspBroker broker;
        public SwiftBrokerConnection(SwiftLspBroker broker) : base(broker.Output, new LspInputStream(broker.Input, null)) { this.broker = broker; }
        protected override void Dispose(bool isDisposing)
        {
            if (isDisposing) broker.Dispose();
            base.Dispose(isDisposing);
        }
    }

    internal sealed class SwiftMiddleLayer : ILanguageClientMiddleLayer2<JToken>, IDisposable
    {
        public SwiftCodeActions CodeActions { get; } = new SwiftCodeActions();
        public string Workspace { get; set; }
        public Func<Task> FlushFileChanges { get; set; }
        private CancellationTokenSource activeCompletion;
        public bool CanHandle(string methodName) => true;

        public async Task HandleNotificationAsync(string methodName, JToken methodParam, Func<JToken, Task> sendNotification)
        {
            CodeActions.Notification(methodName, methodParam);
            if (methodName == "textDocument/didOpen" && methodParam?["textDocument"] is JObject document)
                document["languageId"] = "swift";
            PrototypeLog.Write("notification " + methodName + " uri=" + (methodParam?["textDocument"]?["uri"] ?? methodParam?["uri"]));
            await sendNotification(methodParam);
        }

        public async Task<JToken> HandleRequestAsync(string methodName, JToken methodParam, Func<JToken, Task<JToken>> sendRequest)
        {
            if (methodName == "textDocument/foldingRange") return new JArray();
            if (methodName == "textDocument/hover") return null;
            if (methodName == "initialize" && Workspace != null && methodParam is JObject initialize)
            {
                var uri = new Uri(Workspace + Path.DirectorySeparatorChar).AbsoluteUri;
                initialize["rootUri"] = uri;
                initialize["rootPath"] = Workspace;
                initialize["workspaceFolders"] = new JArray(new JObject { ["uri"] = uri, ["name"] = Path.GetFileName(Workspace) });
            }
            PrototypeLog.Write("request " + methodName);
            var workspace = SwiftWorkspace.ForUri((string)methodParam?["textDocument"]?["uri"]) ?? Workspace;
            var hints = methodName == "textDocument/inlayHint" ? SwiftSettings.Default.ReadInlayHints(workspace) : null;
            if (hints != null && !hints.Enabled) return new JArray();
            if (methodName.StartsWith("textDocument/", StringComparison.Ordinal)
                && FlushFileChanges != null
                && methodName != "textDocument/completion"
                && !methodName.StartsWith("textDocument/semanticTokens", StringComparison.Ordinal)
                && methodName != "textDocument/publishDiagnostics")
                await FlushFileChanges();
            if (methodName == "textDocument/codeAction") methodParam = CodeActions.Prepare(methodParam);

            CancellationTokenSource cts = null;
            if (methodName == "textDocument/completion")
            {
                var next = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var previous = Interlocked.Exchange(ref activeCompletion, next);
                try { previous?.Cancel(); previous?.Dispose(); } catch { }
                cts = next;
            }
            else if (methodName == "textDocument/signatureHelp")
            {
                cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            }
            else if (methodName == "textDocument/inlayHint")
            {
                cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            }
            else if (methodName != "initialize" && methodName != "textDocument/formatting" && methodName != "textDocument/rangeFormatting")
            {
                cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            }

            JToken result;
            try
            {
                var task = sendRequest(methodParam);
                result = cts != null ? await task.WithCancellation(cts.Token) : await task;
            }
            catch (OperationCanceledException)
            {
                if (methodName == "textDocument/completion" || methodName == "textDocument/inlayHint") return new JArray();
                return null;
            }
            catch (Exception error) when (methodName == "textDocument/formatting" || methodName == "textDocument/rangeFormatting")
            {
                PrototypeLog.Write("Swift formatting failed: " + error.Message);
                throw;
            }
            finally
            {
                if (cts != null && !ReferenceEquals(cts, activeCompletion)) cts.Dispose();
            }

            if (methodName == "initialize" && result?["capabilities"] is JObject capabilities) capabilities["hoverProvider"] = false;
            if (hints != null && result is JArray inlayHints) result = hints.Filter(inlayHints);
            if (methodName == "textDocument/codeAction") result = await CodeActions.CompleteAsync(result);
            PrototypeLog.Write("response " + methodName + " " + result?.ToString(Newtonsoft.Json.Formatting.None));
            return result;
        }

        public void Dispose()
        {
            var cts = Interlocked.Exchange(ref activeCompletion, null);
            try { cts?.Cancel(); cts?.Dispose(); } catch { }
        }
    }
}
