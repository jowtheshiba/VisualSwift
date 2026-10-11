using System.Text;
using Newtonsoft.Json.Linq;

static class ProtocolChecks
{
    public static async Task Run()
    {
        using var destination = new MemoryStream();
        var workspace = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Swift Package Пример"));
        using var adapter = new VisualSwift.LspInputStream(destination, workspace);
        var message = new JObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
            ["params"] = new JObject
            {
                ["capabilities"] = new JObject
                {
                    ["textDocument"] = new JObject
                    {
                        ["completion"] = new JObject { ["dynamicRegistration"] = true, ["completionItem"] = new JObject { ["snippetSupport"] = true } },
                        ["diagnostic"] = new JObject { ["dynamicRegistration"] = true }
                    }
                }
            }
        };
        var body = Encoding.UTF8.GetBytes(message.ToString(Newtonsoft.Json.Formatting.None));
        var frame = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n").Concat(body).ToArray();
        foreach (var value in frame) await adapter.WriteAsync(new[] { value }, 0, 1);
        var bytes = destination.ToArray();
        var headerEnd = Encoding.UTF8.GetString(bytes).IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
        var header = Encoding.ASCII.GetString(bytes, 0, headerEnd);
        var length = int.Parse(header.Split("\r\n")[0].Split(':')[1]);
        if (length != bytes.Length - headerEnd) throw new InvalidOperationException("UTF-8 frame length is incorrect.");
        var adapted = JObject.Parse(Encoding.UTF8.GetString(bytes, headerEnd, length));
        if (adapted["params"]?["capabilities"]?["textDocument"]?["completion"]?["dynamicRegistration"]?.Value<bool>() != false)
            throw new InvalidOperationException("Dynamic completion registration was not disabled.");
        if (adapted["params"]?["capabilities"]?["textDocument"]?["completion"]?["completionItem"]?["snippetSupport"]?.Value<bool>() != true)
            throw new InvalidOperationException("Unrelated completion capabilities were changed.");
        if (adapted["params"]?["capabilities"]?["textDocument"]?["diagnostic"] != null)
            throw new InvalidOperationException("Pull diagnostic capability was not removed.");
        if (adapted["params"]?["rootUri"]?.Value<string>() != new Uri(workspace + Path.DirectorySeparatorChar).AbsoluteUri)
            throw new InvalidOperationException("Workspace URI was not preserved.");
        var notification = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}");
        var notificationFrame = Encoding.ASCII.GetBytes("Content-Length: " + notification.Length + "\r\n\r\n").Concat(notification).ToArray();
        var before = destination.Length;
        var joined = notificationFrame.Concat(notificationFrame).ToArray();
        await adapter.WriteAsync(joined, 0, joined.Length);
        if (destination.Length - before != joined.Length) throw new InvalidOperationException("Combined LSP frames were not forwarded.");
    }
}
