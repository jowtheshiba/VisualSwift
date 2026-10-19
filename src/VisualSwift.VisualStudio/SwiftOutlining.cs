using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace VisualSwift
{
    [Export(typeof(ITaggerProvider)), ContentType("swift"), TagType(typeof(IOutliningRegionTag))]
    internal sealed class SwiftOutlining : ITaggerProvider
    {
        [Import] internal ITextDocumentFactoryService Documents = null;
        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            return buffer.Properties.GetOrCreateSingletonProperty(() => new Regions(buffer, Documents)) as ITagger<T>;
        }

        private sealed class Regions : ITagger<IOutliningRegionTag>, IDisposable
        {
            private readonly ITextBuffer buffer;
            private readonly ITextDocumentFactoryService documents;
            private ITrackingSpan[] regions = Array.Empty<ITrackingSpan>();
            private CancellationTokenSource pending;
            private bool disposed;
            public event EventHandler<SnapshotSpanEventArgs> TagsChanged;
            public Regions(ITextBuffer buffer, ITextDocumentFactoryService documents)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                this.buffer = buffer; this.documents = documents;
                PrototypeLog.Write("Swift outlining editor attached.");
                buffer.Changed += Changed;
                buffer.ContentTypeChanged += ContentChanged;
                documents.TextDocumentDisposed += DocumentClosed;
                SwiftRuntime.Changed += RuntimeChanged;
                Queue();
            }
            private void Changed(object sender, TextContentChangedEventArgs args) { ThreadHelper.ThrowIfNotOnUIThread(); Queue(); }
            private void ContentChanged(object sender, ContentTypeChangedEventArgs args) { if (!args.AfterContentType.IsOfType("swift")) Dispose(); }
            private void DocumentClosed(object sender, TextDocumentEventArgs args) { if (args.TextDocument.TextBuffer == buffer) Dispose(); }
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSSDK007", Justification = "Runtime refresh is observed with FileAndForget.")]
            private void RuntimeChanged() => ThreadHelper.JoinableTaskFactory.RunAsync(async () => { await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); Queue(); }).FileAndForget("VisualSwift/OutliningRuntime");
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSSDK007", Justification = "Refresh tasks are observed with FileAndForget and handle cancellation and server failures.")]
            private void Queue()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (disposed) return;
                pending?.Cancel();
                var cancellation = new CancellationTokenSource(); pending = cancellation;
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        await Task.Delay(1000, cancellation.Token);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation.Token);
                        var snapshot = buffer.CurrentSnapshot;
                        var request = SwiftRuntime.Request;
                        if (request == null || SwiftRuntime.State != "Running") return;
                        if (!documents.TryGetTextDocument(buffer, out var document)) return;
                        var result = await request("textDocument/foldingRange", new { textDocument = new { uri = new Uri(Path.GetFullPath(document.FilePath)).AbsoluteUri } }).WithCancellation(cancellation.Token);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation.Token);
                        if (disposed || cancellation.IsCancellationRequested || snapshot != buffer.CurrentSnapshot || !ReferenceEquals(request, SwiftRuntime.Request)) return;
                        var updated = new List<ITrackingSpan>();
                        foreach (var item in result as JArray ?? new JArray())
                        {
                            var startLine = (int?)item["startLine"] ?? -1; var endLine = (int?)item["endLine"] ?? -1;
                            if (startLine < 0 || endLine <= startLine || endLine >= snapshot.LineCount) continue;
                            var first = snapshot.GetLineFromLineNumber(startLine); var last = snapshot.GetLineFromLineNumber(endLine);
                            var start = first.Start.Position + Math.Min(first.Length, Math.Max(0, (int?)item["startCharacter"] ?? first.Length));
                            var end = last.Start.Position + Math.Min(last.Length, Math.Max(0, (int?)item["endCharacter"] ?? last.Length));
                            if (end > start) updated.Add(snapshot.CreateTrackingSpan(Span.FromBounds(start, end), SpanTrackingMode.EdgeExclusive));
                        }
                        regions = updated.ToArray();
                        PrototypeLog.Write("Swift outlining regions: " + regions.Length);
                        TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception error) { if (!disposed && !cancellation.IsCancellationRequested) PrototypeLog.Write("Swift outlining: " + error.Message); }
                    finally { if (ReferenceEquals(pending, cancellation)) pending = null; cancellation.Dispose(); }
                }).FileAndForget("VisualSwift/Outlining");
            }
            public IEnumerable<ITagSpan<IOutliningRegionTag>> GetTags(NormalizedSnapshotSpanCollection spans)
            {
                if (disposed || spans.Count == 0) yield break;
                foreach (var region in regions)
                {
                    var span = region.GetSpan(spans[0].Snapshot);
                    if (span.Length > 0 && spans.IntersectsWith(span)) yield return new TagSpan<IOutliningRegionTag>(span, new OutliningRegionTag(false, false, "…", span.GetText()));
                }
            }
            public void Dispose()
            {
                if (disposed) return; disposed = true;
                pending?.Cancel();
                buffer.Changed -= Changed;
                buffer.ContentTypeChanged -= ContentChanged;
                documents.TextDocumentDisposed -= DocumentClosed;
                SwiftRuntime.Changed -= RuntimeChanged;
                regions = Array.Empty<ITrackingSpan>();
                buffer.Properties.RemoveProperty(typeof(Regions));
            }
        }
    }
}
