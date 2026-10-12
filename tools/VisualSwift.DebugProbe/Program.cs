using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Newtonsoft.Json.Linq;

if (args.Length != 4) throw new ArgumentException("Usage: DebugProbe <adapter> <executable> <source> <report>");
var report = new JObject();
using var adapter = new DapClient(args[0]);
try
{
    report["capabilities"] = await adapter.Request("initialize", new { adapterID = "swift", clientID = "VisualSwift.DebugProbe", linesStartAt1 = true, columnsStartAt1 = true, pathFormat = "path" });
    var launch = adapter.Request("launch", new { program = Path.GetFullPath(args[1]), cwd = Path.GetDirectoryName(Path.GetFullPath(args[1])), stopOnEntry = false });
    await adapter.Event("initialized");
    var breakpoints = await adapter.Request("setBreakpoints", new { source = new { path = Path.GetFullPath(args[2]) }, breakpoints = new[] { new { line = 5 } } });
    report["breakpoints"] = breakpoints;
    Assert(breakpoints["breakpoints"]![0]!.Value<bool>("verified"), "Source breakpoint did not bind.");
    await adapter.Request("configurationDone", new { });
    await launch;
    var stopped = await adapter.Event("stopped");
    var thread = stopped.Value<int>("threadId");
    var stack = await adapter.Request("stackTrace", new { threadId = thread });
    report["stack"] = stack;
    var frame = stack["stackFrames"]![0]!.Value<int>("id");
    Assert(stack["stackFrames"]![0]!.Value<int>("line") == 5, "Wrong source stop line.");
    var scopes = await adapter.Request("scopes", new { frameId = frame });
    var locals = scopes["scopes"]!.First(s => s.Value<string>("name") == "Locals");
    report["locals"] = await adapter.Request("variables", new { variablesReference = locals.Value<int>("variablesReference") });
    var before = await adapter.Request("evaluate", new { expression = "before", frameId = frame, context = "watch" });
    report["watch"] = before;
    Assert(before.Value<string>("result") == "41", "Swift local expression returned wrong value.");
    await adapter.Request("next", new { threadId = thread });
    await adapter.Event("stopped");
    stack = await adapter.Request("stackTrace", new { threadId = thread });
    frame = stack["stackFrames"]![0]!.Value<int>("id");
    Assert(stack["stackFrames"]![0]!.Value<int>("line") == 6, "Step did not reach the next source line.");
    var value = await adapter.Request("evaluate", new { expression = "self.value", frameId = frame, context = "watch" });
    report["afterStep"] = value;
    Assert(value.Value<string>("result") == "42", "Swift value did not update after stepping.");
    await adapter.Request("continue", new { threadId = thread });
    await adapter.Event("terminated");
    await adapter.Request("disconnect", new { terminateDebuggee = true });
    report["passed"] = true;
    Console.WriteLine("Debug smoke passed: source breakpoint, stack, locals, Swift Watch expression, step and termination.");
}
catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); Environment.ExitCode = 1; Console.Error.WriteLine(error.Message); }
finally { report["messages"] = adapter.Snapshot(); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!); File.WriteAllText(args[3], report.ToString()); }

static void Assert(bool result, string message) { if (!result) throw new InvalidOperationException(message); }

sealed class DapClient : IDisposable
{
    readonly Process process;
    readonly Channel<JObject> incoming = Channel.CreateUnbounded<JObject>();
    readonly List<JObject> deferred = new();
    readonly SemaphoreSlim gate = new(1);
    int sequence;
    readonly List<JObject> messages = new();
    public JArray Snapshot() { lock (messages) return new JArray(messages); }
    public DapClient(string executable)
    {
        process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        _ = process.StandardError.ReadToEndAsync();
        _ = ReadAsync();
    }
    async Task ReadAsync()
    {
        try
        {
            var stream = process.StandardOutput.BaseStream;
            while (true)
            {
                var header = new List<byte>();
                var single = new byte[1];
                while (header.Count < 4 || !header.TakeLast(4).SequenceEqual(new byte[] {13,10,13,10}))
                {
                    if (await stream.ReadAsync(single) == 0) throw new EndOfStreamException("Adapter exited.");
                    header.Add(single[0]);
                }
                var length = int.Parse(Encoding.ASCII.GetString(header.ToArray()).Split("Content-Length: ")[1].Split('\r')[0]);
                var payload = new byte[length];
                await stream.ReadExactlyAsync(payload);
                var message = JObject.Parse(Encoding.UTF8.GetString(payload));
                lock (messages) messages.Add(message);
                await incoming.Writer.WriteAsync(message);
            }
        }
        catch (Exception error) { incoming.Writer.TryComplete(error); }
    }
    public async Task<JObject> Request(string command, object arguments)
    {
        var id = Interlocked.Increment(ref sequence);
        var payload = Encoding.UTF8.GetBytes(new JObject { ["seq"] = id, ["type"] = "request", ["command"] = command, ["arguments"] = JObject.FromObject(arguments) }.ToString(Newtonsoft.Json.Formatting.None));
        await process.StandardInput.BaseStream.WriteAsync(Encoding.ASCII.GetBytes("Content-Length: " + payload.Length + "\r\n\r\n"));
        await process.StandardInput.BaseStream.WriteAsync(payload);
        await process.StandardInput.BaseStream.FlushAsync();
        var response = await Wait(m => m.Value<string>("type") == "response" && m.Value<int>("request_seq") == id);
        if (!response.Value<bool>("success")) throw new InvalidOperationException(command + ": " + response.Value<string>("message"));
        return response["body"] as JObject ?? new JObject();
    }
    public async Task<JObject> Event(string name) => (await Wait(m => m.Value<string>("event") == name))["body"] as JObject ?? new JObject();
    async Task<JObject> Wait(Func<JObject,bool> match)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await gate.WaitAsync(timeout.Token);
        try
        {
            var found = deferred.FindIndex(m => match(m));
            if (found >= 0) { var item = deferred[found]; deferred.RemoveAt(found); return item; }
            while (true) { var item = await incoming.Reader.ReadAsync(timeout.Token); if (match(item)) return item; deferred.Add(item); }
        }
        finally { gate.Release(); }
    }
    public void Dispose() { if (!process.HasExited) process.Kill(true); process.Dispose(); }
}
