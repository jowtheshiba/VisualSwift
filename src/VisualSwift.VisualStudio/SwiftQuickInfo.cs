using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace VisualSwift
{
    [Export(typeof(IAsyncQuickInfoSourceProvider)), Name("Swift Quick Info"), ContentType("swift")]
    internal sealed class SwiftQuickInfo : IAsyncQuickInfoSourceProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null;
        public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer) => textBuffer.Properties.GetOrCreateSingletonProperty(() => new Source(textBuffer, Documents));

        private sealed class Source : IAsyncQuickInfoSource
        {
            private readonly ITextBuffer buffer;
            private readonly ITextDocumentFactoryService documents;
            private bool disposed;
            public Source(ITextBuffer buffer, ITextDocumentFactoryService documents) { this.buffer = buffer; this.documents = documents; }
            public async Task<QuickInfoItem> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken cancellationToken)
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    if (disposed || !buffer.ContentType.IsOfType("swift")) return null;
                    var snapshot = buffer.CurrentSnapshot; var trigger = session.GetTriggerPoint(snapshot);
                    var request = SwiftRuntime.Request;
                    if (trigger == null || request == null || !documents.TryGetTextDocument(buffer, out var document)) return null;
                    var line = trigger.Value.GetContainingLine();
                    var response = await request("textDocument/hover", new { textDocument = new { uri = new Uri(document.FilePath).AbsoluteUri }, position = new { line = line.LineNumber, character = trigger.Value.Position - line.Start.Position } }).WithCancellation(cancellationToken);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    if (disposed || snapshot != buffer.CurrentSnapshot || !ReferenceEquals(request, SwiftRuntime.Request)) return null;
                    var model = SwiftQuickInfoContent.Parse(response?["contents"]);
                    if (model.Signatures.Count == 0 && model.Documentation.Count == 0) return null;
                    var span = ApplicableSpan(snapshot, trigger.Value, response?["range"]);
                    var sections = new List<object>();
                    foreach (var signature in model.Signatures)
                    {
                        var moniker = Icon(signature);
                        sections.Add(new ContainerElement(ContainerElementStyle.Wrapped, new ImageElement(new ImageId(moniker.Guid, moniker.Id)), new ClassifiedTextElement(Classify(signature))));
                    }
                    foreach (var paragraph in model.Documentation) sections.Add(new ClassifiedTextElement(new ClassifiedTextRun("text", paragraph)));
                    PrototypeLog.Write("Swift Quick Info: " + model.Signatures.Count + " signatures, " + model.Documentation.Count + " documentation sections.");
                    return new QuickInfoItem(span, new ContainerElement(ContainerElementStyle.Stacked, sections));
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception error) { PrototypeLog.Write("Swift Quick Info: " + error.Message); return null; }
            }
            private static ITrackingSpan ApplicableSpan(ITextSnapshot snapshot, SnapshotPoint trigger, JToken range)
            {
                var startLine = (int?)range?["start"]?["line"] ?? -1; var endLine = (int?)range?["end"]?["line"] ?? -1;
                if (startLine >= 0 && endLine >= startLine && endLine < snapshot.LineCount)
                {
                    var first = snapshot.GetLineFromLineNumber(startLine); var last = snapshot.GetLineFromLineNumber(endLine);
                    var start = first.Start.Position + Math.Min(first.Length, Math.Max(0, (int?)range["start"]["character"] ?? 0));
                    var end = last.Start.Position + Math.Min(last.Length, Math.Max(0, (int?)range["end"]["character"] ?? 0));
                    if (end > start && trigger.Position >= start && trigger.Position <= end) return snapshot.CreateTrackingSpan(Span.FromBounds(start, end), SpanTrackingMode.EdgeInclusive);
                }
                var left = trigger.Position; var right = left;
                while (left > 0 && (char.IsLetterOrDigit(snapshot[left - 1]) || snapshot[left - 1] == '_')) left--;
                while (right < snapshot.Length && (char.IsLetterOrDigit(snapshot[right]) || snapshot[right] == '_')) right++;
                return snapshot.CreateTrackingSpan(Span.FromBounds(left, right), SpanTrackingMode.EdgeInclusive);
            }
            private static ImageMoniker Icon(string signature)
            {
                if (Regex.IsMatch(signature, @"\b(struct|actor)\b")) return KnownMonikers.StructurePublic;
                if (Regex.IsMatch(signature, @"\bclass\s+\w")) return KnownMonikers.ClassPublic;
                if (Regex.IsMatch(signature, @"\bprotocol\b")) return KnownMonikers.InterfacePublic;
                if (Regex.IsMatch(signature, @"\benum\b")) return KnownMonikers.EnumerationPublic;
                if (Regex.IsMatch(signature, @"\b(func|init|subscript|deinit)\b")) return KnownMonikers.MethodPublic;
                return KnownMonikers.PropertyPublic;
            }
            private static readonly HashSet<string> Keywords = new HashSet<string>(("actor any as associatedtype async await borrowing break case catch class consuming convenience copy default defer deinit didSet distributed do dynamic each else enum extension fallthrough false fileprivate final for func get guard if import in indirect infix init inout internal is isolated lazy let macro mutating nil nonisolated nonmutating open operator optional override package postfix precedencegroup prefix private protocol public repeat required rethrows return self Self sending set some static struct subscript super switch throw throws true try typealias unowned var weak where while willSet").Split(' '), StringComparer.Ordinal);
            private static IEnumerable<ClassifiedTextRun> Classify(string signature)
            {
                foreach (Match token in Regex.Matches(signature, "\\\"(?:\\\\.|[^\\\"\\\\])*\\\"|@[\\p{L}_][\\p{L}\\p{N}_]*|[\\p{L}_][\\p{L}\\p{N}_]*|[0-9]+(?:\\.[0-9]+)?|\\s+|.", RegexOptions.Singleline))
                {
                    var text = token.Value; var kind = "punctuation";
                    if (char.IsWhiteSpace(text[0])) kind = "text";
                    else if (text[0] == '"') kind = "string";
                    else if (char.IsDigit(text[0])) kind = "number";
                    else if (Keywords.Contains(text)) kind = "keyword";
                    else if (char.IsLetter(text[0]) || text[0] == '_')
                    {
                        var remaining = signature.Substring(token.Index + token.Length).TrimStart();
                        kind = char.IsUpper(text[0]) ? "class name" : remaining.StartsWith(":", StringComparison.Ordinal) ? "parameter name" : remaining.StartsWith("(", StringComparison.Ordinal) ? "method name" : "identifier";
                    }
                    yield return new ClassifiedTextRun(kind, text);
                }
            }
            public void Dispose() { if (disposed) return; disposed = true; buffer.Properties.RemoveProperty(typeof(Source)); }
        }
    }
}
