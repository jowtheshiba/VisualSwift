using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;
using Microsoft.VisualStudio;

namespace VisualSwift
{
    [Export(typeof(ITextViewCreationListener))]
    [ContentType("swift")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class SwiftNavigationBar : ITextViewCreationListener
    {
        private static readonly object clientKey = new object();
        [Import] internal IVsEditorAdaptersFactoryService Adapters = null;
        [Import] internal ITextDocumentFactoryService Documents = null;

        public void TextViewCreated(ITextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (view.Properties.ContainsProperty(clientKey)) return;
                var adapter = Adapters.GetViewAdapter(view);
                var codeWindow = GetCodeWindow(adapter);
                var manager = codeWindow as IVsDropdownBarManager;
                if (manager == null) return;
                if (ErrorHandler.Succeeded(manager.GetDropdownBar(out var existing)) && existing != null) return;
                var client = new Client(view, Documents, codeWindow, manager, adapter);
                if (ErrorHandler.Failed(manager.AddDropdownBar(2, client)))
                {
                    client.Dispose();
                    return;
                }
                view.Properties[clientKey] = client;
                PrototypeLog.Write("Swift navigation bar attached.");
            }
            catch (Exception error)
            {
                PrototypeLog.Write("Swift navigation bar attach failed: " + error.Message);
            }
        }

        private IVsCodeWindow GetCodeWindow(IVsTextView adapter)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(adapter is IVsTextViewEx adapterEx)) return null;
            if (!ErrorHandler.Succeeded(adapterEx.GetWindowFrame(out var frameObject))) return null;
            if (!(frameObject is IVsWindowFrame frame)) return null;
            if (ErrorHandler.Failed(frame.QueryViewInterface(typeof(IVsCodeWindow).GUID, out var pointer))) return null;
            try
            {
                return Marshal.GetObjectForIUnknown(pointer) as IVsCodeWindow;
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }

        private sealed class Symbol
        {
            internal string Name = string.Empty;
            internal int Line, Char, Depth;
            internal List<Symbol> Children = new List<Symbol>();
        }

        private sealed class Symbols : IDisposable
        {
            private readonly ITextBuffer buffer;
            private readonly ITextDocumentFactoryService documents;
            private CancellationTokenSource pending;
            private int clients;
            private bool disposed;
            internal Symbol[] Types = Array.Empty<Symbol>();
            internal event Action Updated;

            private Symbols(ITextBuffer buffer, ITextDocumentFactoryService documents)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                this.buffer = buffer;
                this.documents = documents;
                buffer.Changed += Changed;
                buffer.ContentTypeChanged += ContentChanged;
                documents.TextDocumentDisposed += DocumentClosed;
                SwiftRuntime.Changed += RuntimeChanged;
                Queue();
            }

            internal static Symbols Attach(ITextBuffer buffer, ITextDocumentFactoryService documents)
            {
                var symbols = buffer.Properties.GetOrCreateSingletonProperty(() => new Symbols(buffer, documents));
                symbols.clients++;
                return symbols;
            }

            internal void Detach()
            {
                clients--;
                if (clients <= 0) Dispose();
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                pending?.Cancel();
                buffer.Changed -= Changed;
                buffer.ContentTypeChanged -= ContentChanged;
                documents.TextDocumentDisposed -= DocumentClosed;
                SwiftRuntime.Changed -= RuntimeChanged;
                Types = Array.Empty<Symbol>();
                buffer.Properties.RemoveProperty(typeof(Symbols));
                Updated?.Invoke();
            }

            private void Changed(object sender, TextContentChangedEventArgs args) { ThreadHelper.ThrowIfNotOnUIThread(); if (buffer.ContentType.IsOfType("swift")) Queue(); }
            private void ContentChanged(object sender, ContentTypeChangedEventArgs args)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (args.AfterContentType.IsOfType("swift")) Queue();
                else
                {
                    Types = Array.Empty<Symbol>();
                    Updated?.Invoke();
                }
            }
            private void DocumentClosed(object sender, TextDocumentEventArgs args) { if (args.TextDocument.TextBuffer == buffer) Dispose(); }
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSSDK007", Justification = "Runtime refresh is observed with FileAndForget.")]
            private void RuntimeChanged() => ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Queue();
            }).FileAndForget("VisualSwift/NavigationBarRuntime");
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSSDK007", Justification = "Symbol fetches are observed with FileAndForget and handle cancellation and server failures.")]
            private void Queue()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (disposed) return;
                pending?.Cancel();
                var cancellation = new CancellationTokenSource();
                pending = cancellation;
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        await Task.Delay(800, cancellation.Token);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation.Token);
                        var snapshot = buffer.CurrentSnapshot;
                        var request = SwiftRuntime.Request;
                        if (request == null || SwiftRuntime.State != "Running") return;
                        if (!documents.TryGetTextDocument(buffer, out var document) || string.IsNullOrEmpty(document.FilePath)) return;
                        var uri = new Uri(Path.GetFullPath(document.FilePath)).AbsoluteUri;
                        var result = await request("textDocument/documentSymbol", new { textDocument = new { uri } }).WithCancellation(cancellation.Token);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation.Token);
                        if (disposed || cancellation.IsCancellationRequested || snapshot != buffer.CurrentSnapshot || !ReferenceEquals(request, SwiftRuntime.Request)) return;
                        var types = new List<Symbol>();
                        if (result is JArray array)
                        {
                            foreach (var item in array)
                            {
                                var type = Read(item, 0);
                                if (type != null) types.Add(type);
                            }
                        }
                        types.Sort(Compare);
                        Types = types.ToArray();
                        Updated?.Invoke();
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception error)
                    {
                        if (!disposed && !cancellation.IsCancellationRequested) PrototypeLog.Write("Swift navigation bar symbols: " + error.Message);
                    }
                    finally
                    {
                        if (ReferenceEquals(pending, cancellation)) pending = null;
                        cancellation.Dispose();
                    }
                }).FileAndForget("VisualSwift/NavigationBar");
            }

            private static int Compare(Symbol left, Symbol right)
            {
                if (left.Line != right.Line) return left.Line.CompareTo(right.Line);
                return left.Char.CompareTo(right.Char);
            }

            private static Symbol Read(JToken item, int depth)
            {
                var name = (string)item?["name"];
                if (string.IsNullOrWhiteSpace(name)) return null;
                var location = item["selectionRange"]?["start"] ?? item["range"]?["start"] ?? item["location"]?["range"]?["start"];
                var symbol = new Symbol
                {
                    Name = name.Trim(),
                    Line = (int?)location?["line"] ?? -1,
                    Char = (int?)location?["character"] ?? 0,
                    Depth = depth
                };
                if (symbol.Line < 0) return null;
                if (item["children"] is JArray children)
                {
                    foreach (var child in children)
                    {
                        var member = Read(child, depth + 1);
                        if (member != null) symbol.Children.Add(member);
                    }
                    symbol.Children.Sort(Compare);
                }
                return symbol;
            }
        }

        private sealed class Client : IVsDropdownBarClient, IVsDropdownBarClient3, IDisposable
        {
            private const int TypeCombo = 0;
            private const int MemberCombo = 1;
            private const uint EntryStyle = (uint)(DROPDOWNENTRYTYPE.ENTRY_TEXT | DROPDOWNENTRYTYPE.ENTRY_ATTR);
            private static readonly Symbol[] PlaceholderTypes = { new Symbol { Name = "(No types)" } };
            private static readonly Symbol[] PlaceholderMembers = { new Symbol { Name = "(No members)" } };

            private readonly ITextDocumentFactoryService documents;
            private readonly IVsCodeWindow codeWindow;
            private readonly IVsDropdownBarManager manager;
            private ITextView view;
            private IVsTextView adapter;
            private Symbols symbols;
            private IVsDropdownBar attached;
            private int programmatic;
            private bool disposed;
            private int typeIndex, memberIndex;

            public Client(ITextView view, ITextDocumentFactoryService documents, IVsCodeWindow codeWindow, IVsDropdownBarManager manager, IVsTextView adapter)
            {
                this.view = view;
                this.documents = documents;
                this.codeWindow = codeWindow;
                this.manager = manager;
                this.adapter = adapter;
                symbols = Symbols.Attach(view.TextBuffer, documents);
                symbols.Updated += Updated;
                view.Caret.PositionChanged += CaretPositionChanged;
                view.Closed += Closed;
                typeIndex = 0;
                memberIndex = 0;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                view.Caret.PositionChanged -= CaretPositionChanged;
                view.Closed -= Closed;
                if (symbols != null)
                {
                    symbols.Updated -= Updated;
                    symbols.Detach();
                    symbols = null;
                }
                view = null;
            }

            private void Closed(object sender, EventArgs args)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    Dispose();
                    if (adapter == null) return;
                    if (!ErrorHandler.Succeeded(codeWindow.GetPrimaryView(out var primary)) || !ReferenceEquals(primary, adapter)) return;
                    if (!ErrorHandler.Succeeded(manager.GetDropdownBar(out var current)) || !ReferenceEquals(current, attached)) return;
                    manager.RemoveDropdownBar();
                }
                catch (Exception error)
                {
                    PrototypeLog.Write("Swift navigation bar detach failed: " + error.Message);
                }
            }

            private void CaretPositionChanged(object sender, CaretPositionChangedEventArgs args) { ThreadHelper.ThrowIfNotOnUIThread(); SyncFromCaret(); }

            private void Updated()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                ThreadHelper.ThrowIfNotOnUIThread();
                if (disposed) return;
                SyncFromCaret();
            }

            public int SetDropdownBar(IVsDropdownBar dropdownBar)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                attached = dropdownBar;
                SyncFromCaret();
                return 0;
            }

            public int GetComboAttributes(int combo, out uint entries, out uint entryType, out IntPtr imageList)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                entries = (uint)List(combo).Length;
                entryType = EntryStyle;
                imageList = IntPtr.Zero;
                return 0;
            }

            public int GetEntryText(int combo, int entry, out string text)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                text = Label(combo, entry);
                return 0;
            }

            public int GetEntryAttributes(int combo, int entry, out uint attributes)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var list = List(combo);
                var placeholder = ReferenceEquals(list, PlaceholderTypes) || ReferenceEquals(list, PlaceholderMembers);
                attributes = placeholder && entry >= 0 && entry < list.Length ? (uint)DROPDOWNFONTATTR.FONTATTR_GRAY : 0;
                return 0;
            }

            public int GetEntryImage(int combo, int entry, out int image)
            {
                image = -1;
                return 0;
            }

            public int OnItemSelected(int combo, int entry) => 0;

            public int OnItemChosen(int combo, int entry)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var list = List(combo);
                if (programmatic > 0 || entry < 0 || entry >= list.Length) return 0;
                if (combo == TypeCombo)
                {
                    typeIndex = entry;
                    NavigateTo(list[entry]);
                    SyncFromCaret();
                }
                else
                {
                    memberIndex = entry;
                    NavigateTo(list[entry]);
                }
                return 0;
            }

            public int OnComboGetFocus(int combo) => 0;

            public int GetComboTipText(int combo, out string text)
            {
                text = combo == TypeCombo ? "Swift types" : "Swift members";
                return 0;
            }

            public int GetComboWidth(int combo, out int widthPercent)
            {
                widthPercent = combo == TypeCombo ? 40 : 60;
                return 0;
            }

            public int GetAutomationProperties(int combo, out string name, out string id)
            {
                name = combo == TypeCombo ? "Types" : "Members";
                id = combo == TypeCombo ? "VisualSwift.NavBar.Types" : "VisualSwift.NavBar.Members";
                return 0;
            }

            public int GetEntryImage(int combo, int entry, out int image, out IntPtr imageList)
            {
                image = -1;
                imageList = IntPtr.Zero;
                return 0;
            }

            private Symbol[] List(int combo)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var types = symbols != null ? symbols.Types : Array.Empty<Symbol>();
                if (types.Length == 0) return PlaceholderTypes;
                if (combo == TypeCombo) return types;
                var type = types[Math.Max(0, Math.Min(typeIndex, types.Length - 1))];
                if (type.Children.Count == 0) return PlaceholderMembers;
                return type.Children.ToArray();
            }

            private string Label(int combo, int entry)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var list = List(combo);
                if (entry < 0 || entry >= list.Length) return string.Empty;
                var symbol = list[entry];
                return symbol.Depth == 0 ? symbol.Name : new string(' ', 2 * symbol.Depth) + symbol.Name;
            }

            private void SyncFromCaret()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (attached == null || programmatic > 0 || disposed) return;
                var types = List(TypeCombo);
                if (ReferenceEquals(types, PlaceholderTypes)) return;
                var caret = view.Caret.Position.BufferPosition.Position;
                var changed = false;
                var typeTarget = IndexAtOrBefore(types, caret);
                if (typeTarget != typeIndex)
                {
                    typeIndex = typeTarget;
                    changed = true;
                }
                var members = List(MemberCombo);
                if (ReferenceEquals(members, PlaceholderMembers))
                {
                    if (memberIndex != 0)
                    {
                        memberIndex = 0;
                        changed = true;
                    }
                }
                else
                {
                    var memberTarget = IndexAtOrBefore(members, caret);
                    if (memberTarget != memberIndex)
                    {
                        memberIndex = memberTarget;
                        changed = true;
                    }
                }
                if (changed) Refresh();
            }

            private int IndexAtOrBefore(Symbol[] list, int position)
            {
                var target = 0;
                for (var index = 0; index < list.Length; index++)
                {
                    if (Offset(list[index]) <= position) target = index;
                    else break;
                }
                return target;
            }

            private int Offset(Symbol symbol)
            {
                var snapshot = view.TextBuffer.CurrentSnapshot;
                var line = Math.Max(0, Math.Min(symbol.Line, snapshot.LineCount - 1));
                var start = snapshot.GetLineFromLineNumber(line);
                return start.Start.Add(Math.Min(symbol.Char, start.Length));
            }

            private void Refresh()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (attached == null || disposed) return;
                programmatic++;
                try
                {
                    attached.RefreshCombo(TypeCombo, typeIndex);
                    var count = List(MemberCombo).Length;
                    memberIndex = Math.Max(0, Math.Min(memberIndex, Math.Max(0, count - 1)));
                    attached.RefreshCombo(MemberCombo, memberIndex);
                }
                catch (Exception error)
                {
                    PrototypeLog.Write("Swift navigation bar refresh failed: " + error.Message);
                }
                finally
                {
                    programmatic--;
                }
            }

            private void NavigateTo(Symbol symbol)
            {
                if (symbol == null || symbol.Line < 0) return;
                try
                {
                    var snapshot = view.TextBuffer.CurrentSnapshot;
                    var line = Math.Max(0, Math.Min(symbol.Line, snapshot.LineCount - 1));
                    var span = snapshot.GetLineFromLineNumber(line);
                    var point = span.Start.Add(Math.Min(symbol.Char, span.Length));
                    view.Caret.MoveTo(point);
                    view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(point, point));
                }
                catch (Exception error)
                {
                    PrototypeLog.Write("Swift navigation bar navigation failed: " + error.Message);
                }
            }
        }
    }
}
