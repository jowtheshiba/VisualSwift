using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace VisualSwift
{
    internal static class SwiftManifestDependencies
    {
        private sealed class Token
        {
            public int Start, End;
            public string Text, Literal;
        }
        private sealed class Span { public int Start, End; }

        public static string Remove(string source, string workspace, string location, string identity, string packageName, string[] products)
        {
            var tokens = Lex(source);
            var package = tokens.FindIndex(t => t.Text == "Package");
            if (package < 0 || package + 1 >= tokens.Count || tokens[package + 1].Text != "(") throw Unsupported();
            var packageEnd = End(tokens, package + 1);
            var dependencies = ArrayArgument(tokens, package + 1, packageEnd, "dependencies");
            if (dependencies < 0) throw Unsupported();
            var edits = new List<Span>();
            var names = new HashSet<string>(new[] { identity, packageName }.Where(n => n != null), StringComparer.OrdinalIgnoreCase);
            var matched = false;
            foreach (var element in Elements(tokens, dependencies))
            {
                if (tokens[element.Start].Text != "." || tokens[element.Start + 1].Text != "package" || tokens[element.Start + 2].Text != "(") throw Unsupported();
                var call = element.Start + 2;
                var url = LiteralArgument(tokens, call, element.End, "url");
                var path = LiteralArgument(tokens, call, element.End, "path");
                if (!string.Equals(url ?? path, location, StringComparison.OrdinalIgnoreCase) && !SameLocalPath(workspace, path, location)) continue;
                var name = LiteralArgument(tokens, call, element.End, "name");
                if (name != null) names.Add(name);
                edits.Add(Removal(tokens, dependencies, element));
                matched = true;
            }
            if (!matched) throw new InvalidOperationException("The dependency declaration is not a literal URL/path matching the selected package. Edit Package.swift manually.");
            var targets = ArrayArgument(tokens, package + 1, packageEnd, "targets");
            if (targets >= 0)
            {
                foreach (var target in Elements(tokens, targets))
                {
                    var open = target.Start + 2;
                    if (open > target.End || tokens[open].Text != "(") throw Unsupported();
                    var array = ArrayArgument(tokens, open, target.End, "dependencies");
                    if (array < 0) continue;
                    foreach (var element in Elements(tokens, array))
                    {
                        if (tokens[element.Start].Literal != null)
                        {
                            if (products.Contains(tokens[element.Start].Literal, StringComparer.Ordinal)) throw new InvalidOperationException("A target uses this product by name. Replace it with an explicit .product(name:package:) dependency before removing the package.");
                            continue;
                        }
                        if (element.End < element.Start + 3 || tokens[element.Start].Text != ".") throw Unsupported();
                        if (tokens[element.Start + 1].Text != "product") continue;
                        var name = LiteralArgument(tokens, element.Start + 2, element.End, "package");
                        if (name == null) throw Unsupported();
                        if (names.Contains(name)) edits.Add(Removal(tokens, array, element));
                    }
                }
            }
            var merged = new List<Span>();
            foreach (var edit in edits.OrderBy(e => e.Start))
            {
                if (merged.Count > 0 && merged[merged.Count - 1].End >= edit.Start) merged[merged.Count - 1].End = Math.Max(merged[merged.Count - 1].End, edit.End);
                else merged.Add(edit);
            }
            var result = new StringBuilder(source);
            foreach (var edit in merged.OrderByDescending(e => e.Start)) result.Remove(edit.Start, edit.End - edit.Start);
            return result.ToString();
        }

        private static bool SameLocalPath(string workspace, string path, string location)
        {
            if (path == null) return false;
            return string.Equals(System.IO.Path.GetFullPath(System.IO.Path.Combine(workspace, path)), location, StringComparison.OrdinalIgnoreCase);
        }
        private static InvalidOperationException Unsupported() => new InvalidOperationException("This manifest uses computed dependency expressions. Remove the package manually in Package.swift; no changes were applied.");
        private static int End(List<Token> tokens, int open)
        {
            var close = tokens[open].Text == "(" ? ")" : tokens[open].Text == "[" ? "]" : "}";
            for (var index = open + 1; index < tokens.Count; index++)
            {
                if (tokens[index].Text == "(" || tokens[index].Text == "[" || tokens[index].Text == "{") index = End(tokens, index);
                else if (tokens[index].Text == close) return index;
            }
            throw Unsupported();
        }
        private static int Argument(List<Token> tokens, int open, int end, string name)
        {
            for (var index = open + 1; index < end; index++)
            {
                if (tokens[index].Text == name && index + 2 < end && tokens[index + 1].Text == ":") return index + 2;
                if (tokens[index].Text == "(" || tokens[index].Text == "[" || tokens[index].Text == "{") index = End(tokens, index);
            }
            return -1;
        }
        private static string LiteralArgument(List<Token> tokens, int open, int end, string name)
        {
            var index = Argument(tokens, open, end, name);
            if (index < 0) return null;
            if (tokens[index].Literal == null || (index + 1 < end && tokens[index + 1].Text != "," && tokens[index + 1].Text != ")")) throw Unsupported();
            return tokens[index].Literal;
        }
        private static int ArrayArgument(List<Token> tokens, int open, int end, string name)
        {
            var index = Argument(tokens, open, end, name);
            if (index >= 0 && tokens[index].Text != "[") throw Unsupported();
            return index;
        }
        private static IEnumerable<Span> Elements(List<Token> tokens, int array)
        {
            var end = End(tokens, array);
            var start = array + 1;
            for (var index = start; index < end; index++)
            {
                if (tokens[index].Text == "(" || tokens[index].Text == "[" || tokens[index].Text == "{") index = End(tokens, index);
                if (tokens[index].Text != ",") continue;
                if (index > start) yield return new Span { Start = start, End = index - 1 };
                start = index + 1;
            }
            if (start < end) yield return new Span { Start = start, End = end - 1 };
        }
        private static Span Removal(List<Token> tokens, int array, Span element)
        {
            var start = tokens[element.Start].Start;
            var end = tokens[element.End].End;
            if (tokens[element.End + 1].Text == ",") end = tokens[element.End + 1].End;
            else if (element.Start > array + 1 && tokens[element.Start - 1].Text == ",") start = tokens[element.Start - 1].Start;
            return new Span { Start = start, End = end };
        }
        private static List<Token> Lex(string source)
        {
            var tokens = new List<Token>();
            for (var index = 0; index < source.Length;)
            {
                if (char.IsWhiteSpace(source[index])) { index++; continue; }
                if (index + 1 < source.Length && source.Substring(index, 2) == "//")
                {
                    while (index < source.Length && source[index] != '\n') index++;
                    continue;
                }
                if (index + 1 < source.Length && source.Substring(index, 2) == "/*")
                {
                    var depth = 1;
                    index += 2;
                    while (index + 1 < source.Length && depth > 0)
                    {
                        var pair = source.Substring(index, 2);
                        if (pair == "/*") { depth++; index += 2; }
                        else if (pair == "*/") { depth--; index += 2; }
                        else index++;
                    }
                    if (depth != 0) throw Unsupported();
                    continue;
                }
                var start = index;
                if (source[index] == '#')
                {
                    var hashes = 0;
                    while (index < source.Length && source[index] == '#') { hashes++; index++; }
                    if (index >= source.Length || source[index] != '"') throw Unsupported();
                    var body = ++index;
                    var terminator = "\"" + new string('#', hashes);
                    var closing = source.IndexOf(terminator, body, StringComparison.Ordinal);
                    if (closing < 0) throw Unsupported();
                    var value = source.Substring(body, closing - body);
                    if (value.Contains("\\" + new string('#', hashes)) || value.StartsWith("\"\"", StringComparison.Ordinal)) throw Unsupported();
                    index = closing + terminator.Length;
                    tokens.Add(new Token { Start = start, End = index, Text = source.Substring(start, index - start), Literal = value });
                    continue;
                }
                if (source[index] == '"')
                {
                    if (index + 2 < source.Length && source.Substring(index, 3) == "\"\"\"") throw Unsupported();
                    index++;
                    while (index < source.Length && source[index] != '"') { if (source[index] == '\\') index++; index++; }
                    if (index >= source.Length) throw Unsupported();
                    index++;
                    var literal = source.Substring(start, index - start);
                    string value;
                    try { value = JsonConvert.DeserializeObject<string>(literal); }
                    catch (JsonException) { throw Unsupported(); }
                    tokens.Add(new Token { Start = start, End = index, Text = literal, Literal = value });
                }
                else
                {
                    if (source[index] == '#') throw Unsupported();
                    if (char.IsLetter(source[index]) || source[index] == '_') while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] == '_')) index++;
                    else index++;
                    tokens.Add(new Token { Start = start, End = index, Text = source.Substring(start, index - start) });
                }
            }
            return tokens;
        }
    }
}
