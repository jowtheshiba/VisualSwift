using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.BraceCompletion;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace VisualSwift
{
    [Export(typeof(ITextViewCreationListener))]
    [ContentType("swift")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class SwiftEditorSessions : ITextViewCreationListener
    {
        private static readonly List<WeakReference<ITextBuffer>> buffers = new List<WeakReference<ITextBuffer>>();
        private static IContentType plainText;
        [Import] internal IContentTypeRegistryService ContentTypes = null;
        [Import] internal IBraceCompletionManagerFactory BraceCompletionManagerFactory = null;

        public void TextViewCreated(ITextView textView)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            plainText = ContentTypes.GetContentType("text");
            var reference = new WeakReference<ITextBuffer>(textView.TextBuffer);
            buffers.Add(reference);
            textView.Closed += (_, __) => { ThreadHelper.ThrowIfNotOnUIThread(); buffers.Remove(reference); };
            try
            {
                var manager = BraceCompletionManagerFactory != null ? BraceCompletionManagerFactory.TryGetBraceCompletionManager(textView) : null;
                var option = textView.Options.GetOptionValue<bool>(DefaultTextViewOptions.BraceCompletionEnabledOptionId);
                if (!option)
                {
                    textView.Options.SetOptionValue(DefaultTextViewOptions.BraceCompletionEnabledOptionId, true);
                    option = textView.Options.GetOptionValue<bool>(DefaultTextViewOptions.BraceCompletionEnabledOptionId);
                    PrototypeLog.Write("BraceCompletion option forced on for swift view");
                }
                PrototypeLog.Write("BraceCompletion diagnostic: view=" + (textView.TextBuffer.ContentType?.TypeName ?? "?") + " manager=" + (manager != null) + " enabled=" + (manager?.Enabled ?? false) + " option=" + option + " opening=[[" + (manager?.OpeningBraces ?? "") + "]] closing=[[" + (manager?.ClosingBraces ?? "") + "]]");
            }
            catch (Exception error) { PrototypeLog.Write("BraceCompletion diagnostic failed: " + error); }
        }

        internal static IDisposable Suspend()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var scope = new SessionScope();
            if (plainText == null) return scope;
            try
            {
                foreach (var buffer in buffers.Select(reference => reference.TryGetTarget(out var target) ? target : null).Where(buffer => buffer != null).Distinct().ToArray())
                {
                    if (!buffer.ContentType.IsOfType("swift")) continue;
                    scope.Types.Add(buffer, buffer.ContentType);
                    buffer.ChangeContentType(plainText, scope);
                }
                PrototypeLog.Write("Suspended Swift editor sessions: " + scope.Types.Count);
                return scope;
            }
            catch { scope.Dispose(); throw; }
        }

        private sealed class SessionScope : IDisposable
        {
            internal readonly Dictionary<ITextBuffer, IContentType> Types = new Dictionary<ITextBuffer, IContentType>();
            public void Dispose()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                foreach (var pair in Types)
                {
                    try { if (ReferenceEquals(pair.Key.ContentType, plainText)) pair.Key.ChangeContentType(pair.Value, this); }
                    catch (Exception error) { PrototypeLog.Write("Restoring Swift editor session failed: " + error); }
                }
                PrototypeLog.Write("Restored Swift editor sessions: " + Types.Count);
                Types.Clear();
            }
        }
    }
}
