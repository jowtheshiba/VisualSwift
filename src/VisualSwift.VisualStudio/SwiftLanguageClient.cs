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
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace VisualSwift
{
    [Export(typeof(ILanguageClient)), ContentType("swift")]
    public sealed class SwiftLanguageClient : ILanguageClient, ILanguageClientCustomMessage2, IDisposable
    {
        private Process server;
        private readonly SwiftMiddleLayer middleLayer = new SwiftMiddleLayer();

        public string Name => "VisualSwift SourceKit-LSP";
        public IEnumerable<string> ConfigurationSections => new[] { "swift" };
        public object InitializationOptions => null;
        public IEnumerable<string> FilesToWatch => new[] { "**/Package.swift", "**/Package.resolved", "**/*.swift" };
        public bool ShowNotificationOnInitializeFailed => true;
        public object MiddleLayer => middleLayer;
        public object CustomMessageTarget => null;
        public event AsyncEventHandler<EventArgs> StartAsync;
        public event AsyncEventHandler<EventArgs> StopAsync { add { } remove { } }

        public async Task OnLoadedAsync()
        {
            PrototypeLog.Write("Language client loaded.");
            if (StartAsync != null)
                await StartAsync.InvokeAsync(this, EventArgs.Empty);
        }

        public Task<Connection> ActivateAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            DisposeServer();
            var toolchain = Toolchain.Resolve();
            var workspace = Environment.GetEnvironmentVariable("VISUALSWIFT_WORKSPACE");
            if (!string.IsNullOrWhiteSpace(workspace))
            {
                workspace = Path.GetFullPath(workspace);
                if (!File.Exists(Path.Combine(workspace, "Package.swift")))
                    throw new InvalidOperationException("VISUALSWIFT_WORKSPACE must point to a Swift package containing Package.swift.");
            }
            middleLayer.Workspace = workspace;
            var process = new Process { StartInfo = toolchain.CreateServerStartInfo(workspace), EnableRaisingEvents = true };
            process.ErrorDataReceived += (_, args) => { if (args.Data != null) PrototypeLog.Write("server: " + args.Data); };
            process.Exited += (_, __) => PrototypeLog.Write("SourceKit-LSP exited.");
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("SourceKit-LSP could not be started.");
            }
            server = process;
            process.BeginErrorReadLine();
            PrototypeLog.Write("Started SourceKit-LSP pid=" + process.Id + " bin=" + toolchain.BinDirectory);
            var connection = new SwiftServerConnection(process, workspace);
            return Task.FromResult<Connection>(connection);
        }

        public Task OnServerInitializedAsync()
        {
            PrototypeLog.Write("SourceKit-LSP initialized.");
            return Task.CompletedTask;
        }

        public Task<InitializationFailureContext> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
        {
            PrototypeLog.Write("SourceKit-LSP initialization failed: " + initializationState.InitializationException);
            DisposeServer();
            return Task.FromResult(new InitializationFailureContext
            {
                FailureMessage = "SourceKit-LSP initialization failed. Check the selected Swift toolchain and the logs in %TEMP%\\VisualSwift."
            });
        }

        public Task AttachForCustomMessageAsync(JsonRpc rpc) => Task.CompletedTask;

        public void Dispose() => DisposeServer();

        private void DisposeServer()
        {
            var process = Interlocked.Exchange(ref server, null);
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
            var result = await sendRequest(methodParam);
            PrototypeLog.Write("response " + methodName + " " + result?.ToString(Newtonsoft.Json.Formatting.None));
            return result;
        }
    }
}
