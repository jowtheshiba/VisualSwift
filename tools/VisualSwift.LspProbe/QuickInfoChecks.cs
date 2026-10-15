using Newtonsoft.Json.Linq;
using VisualSwift;

static class QuickInfoChecks
{
    public static void Run()
    {
        var constructor = SwiftQuickInfoContent.Parse(new JArray(new JObject { ["language"] = "swift", ["value"] = "struct CppMath.Calculator" }, new JObject { ["language"] = "swift", ["value"] = "init(_ initialValue: Int32)" }));
        if (constructor.Signatures.Count != 2 || constructor.Documentation.Count != 0) throw new InvalidOperationException("Constructor Quick Info lost its containing type or initializer.");
        var multiple = SwiftQuickInfoContent.Parse(new JObject { ["kind"] = "markdown", ["value"] = "# Multiple results\n\n```swift\nstruct CppMath.Calculator\n```\n\n---\n\n```swift\ninit(_ initialValue: Int32)\n```" });
        if (multiple.Signatures.Count != 2 || multiple.Documentation.Count != 0) throw new InvalidOperationException("SourceKit's multiple-result wrapper leaked into native Quick Info.");
        var markdown = SwiftQuickInfoContent.Parse(new JObject { ["kind"] = "markdown", ["value"] = "```swift\nfunc add(_ amount: Int32) -> Int32\n```\n\nAdds **amount** to the `value`.\n\n[Reference](https://swift.org)\n\n```swift\nfunc add(_ amount: Int32) -> Int32\n```" });
        if (markdown.Signatures.Count != 1 || markdown.Documentation.Count != 2 || !markdown.Documentation[0].Contains("Adds amount to the value.") || !markdown.Documentation[1].Contains("https://swift.org")) throw new InvalidOperationException("Quick Info did not retain documentation or deduplicate signatures.");
        var plain = SwiftQuickInfoContent.Parse(new JObject { ["kind"] = "plaintext", ["value"] = "Values containing `literal` markers.\n\nSecond paragraph." });
        if (plain.Signatures.Count != 0 || plain.Documentation.Count != 2 || !plain.Documentation[0].Contains("`literal`")) throw new InvalidOperationException("Plaintext Quick Info was treated as Markdown.");
        var multiline = SwiftQuickInfoContent.Parse(new JObject { ["kind"] = "markdown", ["value"] = "```swift\nfunc calculate(\n    value: Int\n) -> Int\n```" });
        if (multiline.Signatures.Count != 1 || !multiline.Signatures[0].Contains("\n    value: Int")) throw new InvalidOperationException("Multiline signature formatting was lost.");
        if (SwiftQuickInfoContent.Parse(JValue.CreateNull()).Signatures.Count != 0) throw new InvalidOperationException("Null hover produced a signature.");
    }
}
