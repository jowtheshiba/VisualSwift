using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

namespace VisualSwift
{
    internal static class SwiftContentTypes
    {
        [Export, Name("swift"), BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
        internal static ContentTypeDefinition Swift = null;

        [Export, FileExtension(".swift"), ContentType("swift")]
        internal static FileExtensionToContentTypeDefinition SwiftFile = null;

        [Export, FileExtension(".swiftinterface"), ContentType("swift")]
        internal static FileExtensionToContentTypeDefinition SwiftInterfaceFile = null;
    }
}
