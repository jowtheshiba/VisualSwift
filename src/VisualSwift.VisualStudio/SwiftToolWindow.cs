using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace VisualSwift
{
    [Guid("c00402ae-65f7-4b3b-b8d9-aec17b0b631a")]
    public sealed class SwiftToolWindow : ToolWindowPane
    {
        public SwiftToolWindow() : base(null)
        {
            Caption = "Swift";
            Content = new SwiftWindowControl();
        }
    }
}
