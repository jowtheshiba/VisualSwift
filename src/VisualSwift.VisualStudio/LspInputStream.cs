using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VisualSwift
{
    internal sealed class LspInputStream : Stream
    {
        private readonly Stream destination;
        private readonly string workspace;
        private readonly MemoryStream pending = new MemoryStream();
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1);

        public LspInputStream(Stream destination, string workspace)
        {
            this.destination = destination;
            this.workspace = workspace;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => destination.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD002", Justification = "Stream requires a synchronous Write implementation; all asynchronous continuations avoid the calling context.")]
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                pending.Position = pending.Length;
                pending.Write(buffer, offset, count);
                if (pending.Length > 32 * 1024 * 1024) throw new InvalidDataException("LSP message is too large.");
                while (true)
                {
                    var bytes = pending.ToArray();
                    var headerEnd = -1;
                    for (var index = 0; index + 3 < bytes.Length; index++)
                        if (bytes[index] == 13 && bytes[index + 1] == 10 && bytes[index + 2] == 13 && bytes[index + 3] == 10)
                        {
                            headerEnd = index + 4;
                            break;
                        }
                    if (headerEnd < 0) return;
                    var header = Encoding.ASCII.GetString(bytes, 0, headerEnd);
                    var lengthLine = header.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    var length = int.Parse(lengthLine.Substring(lengthLine.IndexOf(':') + 1).Trim());
                    if (length < 0 || length > 32 * 1024 * 1024) throw new InvalidDataException("Invalid LSP content length.");
                    if (bytes.Length - headerEnd < length) return;
                    var message = JObject.Parse(Encoding.UTF8.GetString(bytes, headerEnd, length));
                    if (message.Value<string>("method") == "initialize") AdaptInitialize(message);
                    var body = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
                    var newHeader = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
                    await destination.WriteAsync(newHeader, 0, newHeader.Length, cancellationToken).ConfigureAwait(false);
                    await destination.WriteAsync(body, 0, body.Length, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    pending.SetLength(0);
                    pending.Write(bytes, headerEnd + length, bytes.Length - headerEnd - length);
                }
            }
            finally { gate.Release(); }
        }

        private void AdaptInitialize(JObject message)
        {
            if (!(message["params"] is JObject parameters)) throw new InvalidDataException("LSP initialize parameters are missing.");
            PrototypeLog.Write("Original initialize " + parameters.ToString(Formatting.None));
            if (parameters["capabilities"] is JObject capabilities)
            {
                foreach (var property in capabilities.Descendants().OfType<JProperty>().Where(property => property.Name == "dynamicRegistration").ToArray())
                    property.Value = false;
                if (capabilities["textDocument"] is JObject textDocument)
                {
                    textDocument.Remove("diagnostic");
                    SwiftCodeActions.AdaptCapabilities(textDocument);
                    textDocument["callHierarchy"] = new JObject { ["dynamicRegistration"] = false };
                    textDocument["typeHierarchy"] = new JObject { ["dynamicRegistration"] = false };
                    if (textDocument["semanticTokens"]?["requests"] is JObject requests)
                    {
                        if (requests["full"] is JObject full) full["delta"] = true;
                        else if (requests["full"] != null) requests["full"] = new JObject { ["delta"] = true };
                    }
                }
            }
            if (workspace != null)
            {
                var uri = new Uri(workspace + Path.DirectorySeparatorChar).AbsoluteUri;
                parameters["rootUri"] = uri;
                parameters["rootPath"] = workspace;
                parameters["workspaceFolders"] = new JArray(new JObject { ["uri"] = uri, ["name"] = Path.GetFileName(workspace) });
            }
            PrototypeLog.Write("Adapted initialize " + parameters.ToString(Formatting.None));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                destination.Dispose();
                pending.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
