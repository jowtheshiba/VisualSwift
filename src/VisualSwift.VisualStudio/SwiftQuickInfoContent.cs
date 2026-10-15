#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace VisualSwift
{
    internal sealed class SwiftQuickInfoContent
    {
        public List<string> Signatures { get; } = new List<string>();
        public List<string> Documentation { get; } = new List<string>();
        public static SwiftQuickInfoContent Parse(JToken contents)
        {
            var result = new SwiftQuickInfoContent();
            result.Append(contents);
            return result;
        }
        private void Append(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return;
            if (value is JArray array) { foreach (var item in array) Append(item); return; }
            if (value is JObject marked)
            {
                var text = (string)marked["value"] ?? "";
                if (marked["language"] != null) AddSignature(text);
                else ParseText(text, (string)marked["kind"] != "plaintext");
            }
            else if (value.Type == JTokenType.String) ParseText(value.Value<string>(), true);
        }
        private void ParseText(string text, bool markdown)
        {
            var code = false; var fence = ""; var signature = new List<string>(); var paragraph = new List<string>();
            Action flush = () =>
            {
                if (paragraph.Count == 0) return;
                var documentation = string.Join("\n", paragraph).Trim();
                if (documentation.Length > 0 && !Documentation.Contains(documentation)) Documentation.Add(documentation);
                paragraph.Clear();
            };
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                var trimmed = line.Trim();
                if (markdown && !code && (Regex.IsMatch(trimmed, @"^#{1,6}\s+Multiple results\s*$") || Regex.IsMatch(trimmed, @"^(-{3,}|\*{3,}|_{3,})$"))) { flush(); continue; }
                if (markdown && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
                {
                    var current = trimmed.Substring(0, 3);
                    if (!code) { flush(); code = true; fence = current; }
                    else if (current == fence) { AddSignature(string.Join("\n", signature)); signature.Clear(); code = false; }
                    continue;
                }
                if (code) signature.Add(line);
                else if (trimmed.Length == 0) flush();
                else if (Regex.IsMatch(trimmed, @"^(?:(?:public|private|internal|fileprivate|open|final|static|class|nonisolated|mutating|override)\s+)*(?:struct|class|enum|protocol|actor|func|init|deinit|let|var|typealias|subscript|extension)\b"))
                { flush(); AddSignature(trimmed); }
                else
                {
                    var plain = markdown ? Regex.Replace(line, @"!?\[([^\]]+)\]\(([^)]+)\)", "$1 ($2)") : line;
                    if (markdown) plain = Regex.Replace(Regex.Replace(plain, @"^\s*#{1,6}\s+", ""), @"(`+|\*\*|__)", "");
                    paragraph.Add(plain);
                }
            }
            if (signature.Count > 0) AddSignature(string.Join("\n", signature));
            flush();
        }
        private void AddSignature(string value)
        {
            var signature = value.Trim();
            if (signature.Length > 0 && !Signatures.Contains(signature, StringComparer.Ordinal)) Signatures.Add(signature);
        }
    }
}
