#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VisualSwift
{
    internal sealed class SwiftLspBroker : IDisposable
    {
        private readonly string[] roots;
        private readonly Func<string, CancellationToken, Task<SwiftLspEndpoint>> start;
        private readonly Action<string, string, string> status;
        private readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Session> requests = new ConcurrentDictionary<string, Session>();
        private readonly ConcurrentDictionary<string, Tuple<Session, JToken, int>> callbacks = new ConcurrentDictionary<string, Tuple<Session, JToken, int>>();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly SwiftMessageOutput output = new SwiftMessageOutput();
        private readonly SwiftMessageInput input;
        private readonly SemaphoreSlim receive = new SemaphoreSlim(1);
        private readonly object gate = new object();
        private string[] types, modifiers;
        private JObject configuration;
        private int nextCallback;
        private bool disposed;
        public Stream Input => input;
        public Stream Output => output;

        public SwiftLspBroker(IEnumerable<string> roots, Func<string, CancellationToken, Task<SwiftLspEndpoint>> start, Action<string, string, string> status = null)
        {
            this.roots = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            this.start = start; this.status = status;
            input = new SwiftMessageInput(ReceiveAsync);
        }

        private Session[] All() { lock (gate) return sessions.Values.ToArray(); }
        private Session Route(JToken parameters)
        {
            if (parameters is JArray positional) parameters = positional.Count == 1 ? positional[0] : null;
            if (parameters != null && !(parameters is JObject)) return All().FirstOrDefault();
            var tagged = (string)(parameters?["data"] as JObject)?["__visualswiftSession"];
            if (tagged != null) return All().FirstOrDefault(session => session.Root == tagged) ?? throw new InvalidOperationException("The Swift project is no longer loaded.");
            var command = (string)parameters?["command"];
            if (command != null && command.StartsWith("visualswift/", StringComparison.Ordinal))
                return All().FirstOrDefault(session => command.StartsWith(session.CommandPrefix, StringComparison.Ordinal));
            var progress = parameters?["token"]?.Type == JTokenType.String ? (string)parameters["token"] : null;
            if (progress?.StartsWith("visualswift/", StringComparison.Ordinal) == true) return All().FirstOrDefault(session => progress.StartsWith(session.CommandPrefix, StringComparison.Ordinal));
            var uri = (string)(parameters?["textDocument"]?["uri"] ?? parameters?["item"]?["uri"] ?? parameters?["uri"] ?? parameters?["location"]?["uri"]);
            if (uri != null && Uri.TryCreate(uri, UriKind.Absolute, out var file) && file.IsFile)
            {
                var path = Path.GetFullPath(file.LocalPath);
                var matched = All().OrderByDescending(session => session.Root.Length).FirstOrDefault(session => path.StartsWith(session.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                if (matched != null) return matched;
                throw new InvalidOperationException("This file does not belong to a loaded Swift package: " + path);
            }
            return All().FirstOrDefault();
        }

        private async Task ReceiveAsync(JObject message)
        {
            await receive.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                var method = (string)message["method"];
                var id = message["id"];
                if (method == null && id != null)
                {
                    if (callbacks.TryRemove(id.ToString(), out var callback))
                    {
                        var response = (JObject)message.DeepClone(); response["id"] = callback.Item2;
                        try { await callback.Item1.SendCallbackAsync(response, callback.Item3).ConfigureAwait(false); }
                        catch (IOException error) { PrototypeLog.Write("Swift callback connection closed: " + error.Message); }
                    }
                    return;
                }
                try
                {
                    if (method == "initialize") { _ = InitializeFrontendAsync(message); return; }
                    if (method == "shutdown") { Reply(id, JValue.CreateNull()); return; }
                    if (method == "exit") { Dispose(); return; }
                    if (method == "$/cancelRequest")
                    {
                        if (requests.TryGetValue(message["params"]?["id"]?.ToString() ?? "", out var cancelled)) await cancelled.SendAsync(message).ConfigureAwait(false);
                        return;
                    }
                    if (method == "workspace/symbol" || method == "workspace/diagnostic")
                    {
                        _ = AggregateAsync(message); return;
                    }
                    if (method == "workspace/didChangeWatchedFiles")
                    {
                        foreach (var group in (message["params"]?["changes"] as JArray ?? new JArray()).GroupBy(change => Route(change)))
                        {
                            if (group.Key != null) await group.Key.ForwardAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = new JObject { ["changes"] = new JArray(group.Select(item => item.DeepClone())) } }).ConfigureAwait(false);
                        }
                        return;
                    }
                    if (method == "initialized" || method == "workspace/didChangeConfiguration" || method == "workspace/didChangeWorkspaceFolders")
                    {
                        if (method == "workspace/didChangeConfiguration") configuration = (JObject)message.DeepClone();
                        if (method != "workspace/didChangeWorkspaceFolders") foreach (var session in All()) await session.ForwardAsync(message).ConfigureAwait(false);
                        return;
                    }
                    var destination = Route(message["params"]);
                    if (destination == null) throw new InvalidOperationException("No Swift language server is available.");
                    var forwarded = (JObject)message.DeepClone();
                    Unwrap(forwarded["params"], destination);
                    if (id != null) requests[id.ToString()] = destination;
                    await destination.ForwardAsync(forwarded).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    PrototypeLog.Write("Swift project routing [" + method + "]: " + error.Message);
                    if (id != null) { requests.TryRemove(id.ToString(), out _); Error(id, error.Message); }
                }
            }
            finally { receive.Release(); }
        }

        private async Task InitializeFrontendAsync(JObject message)
        {
            try { await InitializeAsync(message).ConfigureAwait(false); }
            catch (Exception error) { Error(message["id"], error.Message); }
        }

        private async Task InitializeAsync(JObject message)
        {
            if (roots.Length == 0) throw new InvalidOperationException("Open a Swift package or solution containing Swift projects.");
            var initialized = new List<Tuple<Session, JObject>>();
            foreach (var root in roots)
            {
                Session session = null;
                try
                {
                    status?.Invoke(root, "Starting", null);
                    var endpoint = await start(root, lifetime.Token).ConfigureAwait(false);
                    session = new Session(this, root, initialized.Count, endpoint);
                    var parameters = (JObject)message["params"].DeepClone();
                    var uri = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri;
                    parameters["rootUri"] = uri; parameters["rootPath"] = root;
                    parameters["workspaceFolders"] = new JArray(new JObject { ["uri"] = uri, ["name"] = Path.GetFileName(root) });
                    session.InitializeParameters = parameters;
                    var result = (JObject)await session.RequestAsync("initialize", parameters).ConfigureAwait(false);
                    lock (gate) sessions[root] = session;
                    initialized.Add(Tuple.Create(session, result));
                    status?.Invoke(root, "Running", null);
                }
                catch (Exception error) { session?.Dispose(); status?.Invoke(root, "Error", error.Message); PrototypeLog.Write("Swift server for " + root + ": " + error); }
            }
            if (initialized.Count == 0) throw new InvalidOperationException("No Swift project language server could be initialized. Check each project's toolchain.");
            types = initialized.SelectMany(item => (item.Item2["capabilities"]?["semanticTokensProvider"]?["legend"]?["tokenTypes"] as JArray ?? new JArray()).Values<string>()).Distinct().ToArray();
            modifiers = initialized.SelectMany(item => (item.Item2["capabilities"]?["semanticTokensProvider"]?["legend"]?["tokenModifiers"] as JArray ?? new JArray()).Values<string>()).Distinct().ToArray();
            foreach (var item in initialized)
            {
                item.Item1.SetLegend(item.Item2["capabilities"]?["semanticTokensProvider"]?["legend"]);
                item.Item1.ArmRecovery();
            }
            var combined = (JObject)initialized[0].Item2.DeepClone();
            var capabilities = (JObject)combined["capabilities"];
            if (capabilities["semanticTokensProvider"] is JObject provider)
            {
                provider["legend"] = new JObject { ["tokenTypes"] = new JArray(types), ["tokenModifiers"] = new JArray(modifiers) };
                provider["full"] = new JObject { ["delta"] = true };
            }
            if (capabilities["workspace"] is JObject workspace) workspace["workspaceFolders"] = new JObject { ["supported"] = true, ["changeNotifications"] = false };
            var commands = new JArray();
            foreach (var item in initialized) foreach (var command in item.Item2["capabilities"]?["executeCommandProvider"]?["commands"] as JArray ?? new JArray()) commands.Add(item.Item1.CommandPrefix + command.Value<string>());
            if (commands.Count > 0) capabilities["executeCommandProvider"] = new JObject { ["commands"] = commands };
            Reply(message["id"], combined);
        }

        private async Task AggregateAsync(JObject message)
        {
            try
            {
                var method = (string)message["method"];
                var items = new JArray();
                foreach (var session in All())
                {
                    if (!session.Available) continue;
                    var result = await session.RequestAsync(method, message["params"]).ConfigureAwait(false);
                    var values = method == "workspace/diagnostic" ? result?["items"] as JArray : result as JArray;
                    foreach (var item in values ?? new JArray()) { var copy = item.DeepClone(); TagData(copy, session); items.Add(copy); }
                }
                Reply(message["id"], method == "workspace/diagnostic" ? (JToken)new JObject { ["items"] = items } : items);
            }
            catch (Exception error) { Error(message["id"], error.Message); }
        }

        private void Reply(JToken id, JToken result) => output.Enqueue(new JObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result ?? JValue.CreateNull() });
        private void Error(JToken id, string message) => output.Enqueue(new JObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JObject { ["code"] = -32603, ["message"] = message } });
        private static void TagData(JToken value, Session session)
        {
            if (value is JObject item) item["data"] = new JObject { ["__visualswiftSession"] = session.Root, ["value"] = item["data"]?.DeepClone() ?? JValue.CreateNull() };
        }
        private static void Unwrap(JToken value, Session session)
        {
            if (!(value is JObject item)) return;
            if (item["data"] is JObject data && data["__visualswiftSession"] != null)
            {
                if (data["value"]?.Type == JTokenType.Null) item.Remove("data"); else item["data"] = data["value"]?.DeepClone();
            }
            var command = (string)item["command"];
            if (command?.StartsWith(session.CommandPrefix, StringComparison.Ordinal) == true) item["command"] = command.Substring(session.CommandPrefix.Length);
            var token = item["token"]?.Type == JTokenType.String ? (string)item["token"] : null;
            if (token?.StartsWith(session.CommandPrefix, StringComparison.Ordinal) == true) item["token"] = JToken.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(token.Substring(session.CommandPrefix.Length))));
        }

        private void Emit(Session session, JObject message, string method)
        {
            if (disposed) return;
            if ((string)message["method"] == "$/progress" || (string)message["method"] == "window/workDoneProgress/create")
            {
                var progress = message["params"] as JObject;
                if (progress?["token"] != null) progress["token"] = session.CommandPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(progress["token"].ToString(Formatting.None)));
            }
            if (message["method"] != null && message["id"] != null)
            {
                if ((string)message["method"] == "workspace/workspaceFolders")
                {
                    session.Observe(session.SendAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = message["id"].DeepClone(), ["result"] = new JArray(new JObject { ["uri"] = new Uri(session.Root + Path.DirectorySeparatorChar).AbsoluteUri, ["name"] = Path.GetFileName(session.Root) }) })); return;
                }
                var key = "visualswift.callback." + Interlocked.Increment(ref nextCallback);
                callbacks[key] = Tuple.Create(session, message["id"].DeepClone(), session.Generation); message["id"] = key;
            }
            else if (message["id"] != null)
            {
                requests.TryRemove(message["id"].ToString(), out _);
                var result = message["result"];
                if (method == "textDocument/semanticTokens/full" || method == "textDocument/semanticTokens/range") session.MapTokens(result?["data"] as JArray);
                else if (method == "textDocument/semanticTokens/full/delta")
                {
                    session.MapTokens(result?["data"] as JArray);
                    if (result?["edits"] is JArray edits)
                    {
                        foreach (var edit in edits) session.MapTokens(edit?["data"] as JArray);
                    }
                }
                if (method == "textDocument/completion")
                    foreach (var item in (result as JArray ?? result?["items"] as JArray ?? new JArray())) TagData(item, session);
                if (method == "textDocument/inlayHint" || method == "textDocument/codeAction" || method == "workspace/symbol")
                    foreach (var item in result as JArray ?? new JArray()) TagData(item, session);
                if (method == "completionItem/resolve" || method == "inlayHint/resolve" || method == "codeAction/resolve") TagData(result, session);
                if (method == "textDocument/codeAction" || method == "codeAction/resolve")
                    foreach (var item in result is JArray array ? array : new JArray(result)) PrefixCommand(item, session);
            }
            output.Enqueue(message);
        }
        private static void PrefixCommand(JToken item, Session session)
        {
            var command = item?["command"];
            if (command?.Type == JTokenType.String) item["command"] = session.CommandPrefix + command.Value<string>();
            else if (command is JObject nested && nested["command"]?.Type == JTokenType.String) nested["command"] = session.CommandPrefix + (string)nested["command"];
        }
        public Task RestartProjectAsync(string root)
        {
            lock (gate)
            {
                if (disposed) return Task.CompletedTask;
                if (!sessions.TryGetValue(root, out var session)) throw new InvalidOperationException("The Swift project is no longer loaded: " + root);
                return session.RestartAsync(false);
            }
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            lifetime.Cancel(); foreach (var session in All()) session.Dispose(); output.Complete();
        }

        private sealed class Session : IDisposable
        {
            private readonly SwiftLspBroker owner;
            private SwiftLspEndpoint endpoint;
            private readonly object stateGate = new object();
            private readonly Dictionary<string, JObject> documents = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, JToken> watchedChanges = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
            private readonly Queue<DateTime> attempts = new Queue<DateTime>();
            private Task recovery;
            private Exception failure;
            private bool armed;
            private bool available = true;
            private int generation;
            public JObject InitializeParameters { get; set; }
            public bool Available { get { lock (stateGate) return available && failure == null && !disposed; } }
            public int Generation { get { lock (stateGate) return generation; } }
            private readonly SemaphoreSlim writes = new SemaphoreSlim(1);
            private readonly ConcurrentDictionary<string, TaskCompletionSource<JToken>> pending = new ConcurrentDictionary<string, TaskCompletionSource<JToken>>();
            private readonly ConcurrentDictionary<string, string> methods = new ConcurrentDictionary<string, string>();
            private int nextId;
            private int[] typeMap, modifierMap;
            private bool disposed;
            public string Root { get; }
            public string CommandPrefix { get; }
            public Session(SwiftLspBroker owner, string root, int index, SwiftLspEndpoint endpoint)
            {
                this.owner = owner; Root = root; CommandPrefix = "visualswift/" + index + "/"; this.endpoint = endpoint;
                Observe(ReadAsync(endpoint, generation));
            }
            public void ArmRecovery()
            {
                lock (stateGate) { armed = true; if (failure != null) Observe(RestartAsync(true)); }
            }
            public Task RestartAsync(bool automatic)
            {
                lock (stateGate)
                {
                    if (disposed || owner.disposed) return Task.CompletedTask;
                    if (recovery != null && !recovery.IsCompleted) return recovery;
                    available = false;
                    recovery = Task.Run(() => RecoverAsync(automatic));
                    return recovery;
                }
            }
            private void FailRequests(Exception error)
            {
                foreach (var completion in pending.Values) completion.TrySetException(error);
                foreach (var request in owner.requests.Where(item => ReferenceEquals(item.Value, this)).ToArray())
                    if (owner.requests.TryRemove(request.Key, out _)) owner.Error(JToken.Parse(request.Key), error.Message);
                foreach (var callback in owner.callbacks.Where(item => ReferenceEquals(item.Value.Item1, this)).ToArray()) owner.callbacks.TryRemove(callback.Key, out _);
                methods.Clear();
            }
            private async Task RecoverAsync(bool automatic)
            {
                try
                {
                    if (!automatic) { lock (stateGate) attempts.Clear(); }
                    while (!owner.lifetime.IsCancellationRequested)
                    {
                        int attempt;
                        lock (stateGate)
                        {
                            var now = DateTime.UtcNow;
                            while (attempts.Count > 0 && now - attempts.Peek() > TimeSpan.FromMinutes(2)) attempts.Dequeue();
                            if (attempts.Count >= 3) throw new InvalidOperationException("SourceKit-LSP recovery stopped after 3 attempts in 2 minutes. Use Restart to try again.", failure);
                            attempts.Enqueue(now); attempt = attempts.Count;
                        }
                        owner.status?.Invoke(Root, "Restarting", "Attempt " + attempt + " of 3");
                        PrototypeLog.Write("Restarting SourceKit-LSP for " + Root + " (attempt " + attempt + ").");
                        try
                        {
                            // Retire the reader before closing its streams; late output must not reach the IDE.
                            lock (stateGate) { generation++; available = false; endpoint.Dispose(); }
                            FailRequests(new IOException("SourceKit-LSP is restarting for " + Root));
                            if (automatic || attempt > 1) await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), owner.lifetime.Token).ConfigureAwait(false);
                            var replacement = await owner.start(Root, owner.lifetime.Token).ConfigureAwait(false);
                            lock (stateGate)
                            {
                                if (disposed || owner.disposed) { replacement.Dispose(); return; }
                                endpoint = replacement; failure = null;
                                Observe(ReadAsync(replacement, generation));
                            }
                            var result = await RequestAsync("initialize", InitializeParameters).ConfigureAwait(false);
                            SetLegend(result?["capabilities"]?["semanticTokensProvider"]?["legend"]);
                            await SendAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = "initialized", ["params"] = new JObject() }).ConfigureAwait(false);
                            // Only replay takes the frontend gate. Initialization callbacks must remain routable.
                            await owner.receive.WaitAsync(owner.lifetime.Token).ConfigureAwait(false);
                            try
                            {
                                if (owner.configuration != null) await SendAsync(owner.configuration).ConfigureAwait(false);
                                if (watchedChanges.Count > 0)
                                {
                                    await SendAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = "workspace/didChangeWatchedFiles", ["params"] = new JObject { ["changes"] = new JArray(watchedChanges.Values.Select(change => change.DeepClone())) } }).ConfigureAwait(false);
                                    watchedChanges.Clear();
                                }
                                foreach (var document in documents.Values)
                                    await SendAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = "textDocument/didOpen", ["params"] = new JObject { ["textDocument"] = document.DeepClone() } }).ConfigureAwait(false);
                                lock (stateGate)
                                {
                                    if (failure != null) throw new IOException("SourceKit-LSP exited during recovery.", failure);
                                    owner.lifetime.Token.ThrowIfCancellationRequested();
                                    available = true;
                                    owner.status?.Invoke(Root, "Running", null);
                                }
                            }
                            finally { owner.receive.Release(); }
                            return;
                        }
                        catch (OperationCanceledException) when (owner.lifetime.IsCancellationRequested) { return; }
                        catch (Exception error)
                        {
                            lock (stateGate) { failure = error; available = false; }
                            PrototypeLog.Write("Swift recovery failed for " + Root + ": " + error.Message);
                        }
                    }
                }
                catch (Exception error)
                {
                    lock (stateGate)
                    {
                        available = false; generation++; endpoint.Dispose(); FailRequests(error);
                        if (!owner.disposed) owner.status?.Invoke(Root, "Error", error.Message);
                    }
                }
                finally
                {
                    lock (stateGate)
                    {
                        // A crash can arrive between replay and completion of this recovery task.
                        recovery = null;
                        if (available && failure != null && !disposed && !owner.disposed) Observe(RestartAsync(true));
                    }
                }
            }
            public async Task ForwardAsync(JObject message)
            {
                TrackDocument(message);
                if (!Available)
                {
                    if (message["id"] != null) throw new InvalidOperationException("SourceKit-LSP is unavailable for " + Root + ". Check its status or use Restart.");
                    if ((string)message["method"] == "workspace/didChangeWatchedFiles")
                        foreach (var change in message["params"]?["changes"] as JArray ?? new JArray()) watchedChanges[(string)change["uri"]] = change.DeepClone();
                    return;
                }
                await SendAsync(message).ConfigureAwait(false);
            }
            private void TrackDocument(JObject message)
            {
                var method = (string)message["method"];
                var parameters = message["params"];
                var uri = (string)parameters?["textDocument"]?["uri"];
                if (uri == null) return;
                if (method == "textDocument/didOpen") documents[uri] = (JObject)parameters["textDocument"].DeepClone();
                else if (method == "textDocument/didClose") documents.Remove(uri);
                else if (method == "textDocument/didChange" && documents.TryGetValue(uri, out var document))
                {
                    var text = (string)document["text"] ?? "";
                    foreach (var change in parameters["contentChanges"] as JArray ?? new JArray())
                    {
                        var range = change["range"];
                        if (range == null) text = (string)change["text"] ?? "";
                        else
                        {
                            var start = Offset(text, range["start"]); var end = Offset(text, range["end"]);
                            text = text.Substring(0, start) + (string)change["text"] + text.Substring(end);
                        }
                    }
                    document["text"] = text; document["version"] = parameters["textDocument"]["version"]?.DeepClone();
                }
            }
            private static int Offset(string text, JToken position)
            {
                var offset = 0;
                for (var line = 0; line < (int)position["line"]; line++)
                {
                    while (offset < text.Length && text[offset] != '\r' && text[offset] != '\n') offset++;
                    if (offset == text.Length) return offset;
                    if (text[offset++] == '\r' && offset < text.Length && text[offset] == '\n') offset++;
                }
                var end = offset;
                while (end < text.Length && text[end] != '\r' && text[end] != '\n') end++;
                return Math.Min(end, offset + (int)position["character"]);
            }
            public void SetLegend(JToken legend)
            {
                typeMap = (legend?["tokenTypes"] as JArray ?? new JArray()).Values<string>().Select(type => Array.IndexOf(owner.types, type)).ToArray();
                modifierMap = (legend?["tokenModifiers"] as JArray ?? new JArray()).Values<string>().Select(modifier => Array.IndexOf(owner.modifiers, modifier)).ToArray();
            }
            public void MapTokens(JArray data)
            {
                if (data == null || typeMap == null) return;
                for (var index = 0; index + 4 < data.Count; index += 5)
                {
                    var type = (int)data[index + 3]; if (type >= 0 && type < typeMap.Length) data[index + 3] = typeMap[type];
                    var bits = (int)data[index + 4]; var mapped = 0;
                    for (var bit = 0; bit < modifierMap.Length && bit < 31; bit++) if ((bits & (1 << bit)) != 0 && modifierMap[bit] >= 0 && modifierMap[bit] < 31) mapped |= 1 << modifierMap[bit];
                    data[index + 4] = mapped;
                }
            }
            public async Task<JToken> RequestAsync(string method, JToken parameters)
            {
                var id = "visualswift.internal." + Interlocked.Increment(ref nextId);
                var completion = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously); pending[id] = completion;
                try
                {
                    await SendAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters?.DeepClone() }).ConfigureAwait(false);
                    var timeout = Task.Delay(TimeSpan.FromSeconds(45), owner.lifetime.Token);
                    if (await Task.WhenAny(completion.Task, timeout).ConfigureAwait(false) != completion.Task) { owner.lifetime.Token.ThrowIfCancellationRequested(); throw new TimeoutException("Swift language server request timed out: " + method); }
                    return await completion.Task.ConfigureAwait(false);
                }
                finally { pending.TryRemove(id, out _); }
            }
            public Task SendAsync(JObject message)
            {
                if (message["id"] != null && message["method"] != null) methods[message["id"].ToString()] = (string)message["method"];
                return WriteAsync(message);
            }
            public Task SendCallbackAsync(JObject message, int epoch) => WriteAsync(message, epoch);
            private async Task WriteAsync(JObject message, int? epoch = null)
            {
                var frame = SwiftMessageInput.Frame(message);
                await writes.WaitAsync(owner.lifetime.Token).ConfigureAwait(false);
                try
                {
                    SwiftLspEndpoint current;
                    lock (stateGate)
                    {
                        if (epoch.HasValue && (disposed || epoch.Value != generation)) return;
                        if (disposed) throw new ObjectDisposedException(nameof(Session));
                        if (failure != null) throw new IOException("SourceKit-LSP connection failed.", failure);
                        current = endpoint;
                    }
                    await current.Input.WriteAsync(frame, 0, frame.Length, owner.lifetime.Token).ConfigureAwait(false);
                    await current.Input.FlushAsync(owner.lifetime.Token).ConfigureAwait(false);
                }
                finally { writes.Release(); }
            }
            private async Task ReadAsync(SwiftLspEndpoint current, int epoch)
            {
                var parser = new SwiftMessageInput(message =>
                {
                    lock (stateGate)
                    {
                        if (disposed || epoch != generation) return Task.CompletedTask;
                        var id = message["id"]?.ToString(); var method = (string)message["method"];
                        if (id != null && method == null)
                        {
                            methods.TryRemove(id, out var requested); method = requested;
                            if (pending.TryGetValue(id, out var completion))
                            {
                                if (message["error"] != null) completion.TrySetException(new InvalidOperationException(message["error"].ToString()));
                                else completion.TrySetResult(message["result"] ?? JValue.CreateNull());
                                return Task.CompletedTask;
                            }
                        }
                        owner.Emit(this, message, method); return Task.CompletedTask;
                    }
                });
                try
                {
                    var bytes = new byte[8192]; int count;
                    while ((count = await current.Output.ReadAsync(bytes, 0, bytes.Length, owner.lifetime.Token).ConfigureAwait(false)) != 0) await parser.WriteAsync(bytes, 0, count, owner.lifetime.Token).ConfigureAwait(false);
                    if (!disposed && !owner.disposed) throw new EndOfStreamException("SourceKit-LSP exited for " + Root);
                }
                catch (Exception error)
                {
                    lock (stateGate)
                    {
                        if (disposed || owner.disposed || epoch != generation) return;
                        failure = error;
                        FailRequests(error);
                        owner.status?.Invoke(Root, "Error", error.Message);
                        if (armed) Observe(RestartAsync(true));
                    }
                }
            }
            public void Observe(Task task) { _ = task.ContinueWith(completed => PrototypeLog.Write("Swift project connection: " + completed.Exception), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); }
            public void Dispose()
            {
                lock (stateGate)
                {
                    if (disposed) return; disposed = true; available = false; generation++;
                    FailRequests(new ObjectDisposedException(nameof(Session))); endpoint.Dispose();
                }
            }
        }
    }

    internal sealed class SwiftLspEndpoint : IDisposable
    {
        public Stream Input { get; }
        public Stream Output { get; }
        private readonly Action dispose;
        private int disposed;
        public SwiftLspEndpoint(Stream input, Stream output, Action dispose) { Input = input; Output = output; this.dispose = dispose; }
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) dispose(); }
    }

    internal abstract class SwiftMessageStream : Stream
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }
    }

    internal sealed class SwiftMessageInput : SwiftMessageStream
    {
        private readonly MemoryStream pending = new MemoryStream();
        private readonly Func<JObject, Task> receive;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1);
        public SwiftMessageInput(Func<JObject, Task> receive) { this.receive = receive; }
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD002", Justification = "The Stream synchronous contract delegates to context-free asynchronous framing.")]
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                pending.Position = pending.Length; pending.Write(buffer, offset, count);
                if (pending.Length > 32 * 1024 * 1024) throw new InvalidDataException("LSP message is too large.");
                while (true)
                {
                    var bytes = pending.ToArray(); var end = -1;
                    for (var index = 0; index + 3 < bytes.Length; index++) if (bytes[index] == 13 && bytes[index + 1] == 10 && bytes[index + 2] == 13 && bytes[index + 3] == 10) { end = index + 4; break; }
                    if (end < 0) { if (bytes.Length > 8192) throw new InvalidDataException("LSP header is too large."); return; }
                    var header = Encoding.ASCII.GetString(bytes, 0, end);
                    var length = int.Parse(header.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries).Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim());
                    if (length < 0 || length > 32 * 1024 * 1024) throw new InvalidDataException("Invalid LSP content length.");
                    if (bytes.Length - end < length) return;
                    var message = JObject.Parse(Encoding.UTF8.GetString(bytes, end, length));
                    pending.SetLength(0); pending.Write(bytes, end + length, bytes.Length - end - length);
                    await receive(message).ConfigureAwait(false);
                }
            }
            finally { gate.Release(); }
        }
        internal static byte[] Frame(JObject message)
        {
            var body = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
            var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
            var frame = new byte[header.Length + body.Length]; Buffer.BlockCopy(header, 0, frame, 0, header.Length); Buffer.BlockCopy(body, 0, frame, header.Length, body.Length); return frame;
        }
    }

    internal sealed class SwiftMessageOutput : SwiftMessageStream
    {
        private readonly ConcurrentQueue<byte[]> messages = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim available = new SemaphoreSlim(0);
        private byte[] current;
        private int position;
        private bool completed;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public void Enqueue(JObject message) { if (completed) return; messages.Enqueue(SwiftMessageInput.Frame(message)); available.Release(); }
        public void Complete() { if (completed) return; completed = true; available.Release(); }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD002", Justification = "The Stream synchronous contract delegates to context-free asynchronous queue reads.")]
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (count == 0) return 0;
            while (current == null)
            {
                if (completed && messages.IsEmpty) return 0;
                await available.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (messages.TryDequeue(out var next)) { current = next; position = 0; }
            }
            var length = Math.Min(count, current.Length - position); Buffer.BlockCopy(current, position, buffer, offset, length); position += length;
            if (position == current.Length) current = null; return length;
        }
    }
}
