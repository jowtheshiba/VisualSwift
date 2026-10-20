using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.BraceCompletion;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace VisualSwift
{
    [Export(typeof(IBraceCompletionDefaultProvider))]
    [Name("VisualSwift.SwiftBraceCompletionDefaults")]
    [ContentType("swift")]
    [BracePair('{', '}')]
    [BracePair('(', ')')]
    [BracePair('[', ']')]
    internal sealed class SwiftBraceCompletionDefaults : IBraceCompletionDefaultProvider
    {
    }

    [Export(typeof(IBraceCompletionContextProvider))]
    [Name("VisualSwift.SwiftBraceCompletion")]
    [ContentType("swift")]
    [BracePair('{', '}')]
    [BracePair('(', ')')]
    [BracePair('[', ']')]
    internal sealed class SwiftBraceCompletion : IBraceCompletionContextProvider
    {
        public bool TryCreateContext(ITextView textView, SnapshotPoint openingPoint, char openingBrace, char closingBrace, out IBraceCompletionContext context)
        {
            context = null;
            if (SwiftSourceScanner.IsInsideStringOrComment(openingPoint.Snapshot, openingPoint.Position)) return false;
            context = new SwiftBraceCompletionContext();
            return true;
        }
    }

    internal sealed class SwiftBraceCompletionContext : IBraceCompletionContext
    {
        public void Start(IBraceCompletionSession session) { }
        public void OnReturn(IBraceCompletionSession session) { }
        public void Finish(IBraceCompletionSession session) { }
        public bool AllowOverType(IBraceCompletionSession session) { return true; }
    }
}
