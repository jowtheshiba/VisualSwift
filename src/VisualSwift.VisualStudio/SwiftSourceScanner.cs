using System;
using Microsoft.VisualStudio.Text;

namespace VisualSwift
{
    internal static class SwiftSourceScanner
    {
        internal static bool IsInsideStringOrComment(ITextSnapshot snapshot, int position)
        {
            if (snapshot == null) return false;
            int limit = Math.Min(position, snapshot.Length);
            int index = 0;
            while (index < limit)
            {
                char current = snapshot[index];
                if (current == '/')
                {
                    if (index + 1 < limit && snapshot[index + 1] == '/')
                    {
                        int endOfLine = IndexOfLineEnd(snapshot, index + 2, limit);
                        if (endOfLine == limit) return true;
                        index = endOfLine;
                        continue;
                    }
                    if (index + 1 < limit && snapshot[index + 1] == '*')
                    {
                        int end;
                        if (!TrySkipBlockComment(snapshot, index + 2, limit, out end)) return true;
                        index = end;
                        continue;
                    }
                    index++;
                    continue;
                }
                if (current == '#')
                {
                    int hashes = CountHashes(snapshot, index, limit);
                    if (index + hashes < limit && snapshot[index + hashes] == '"')
                    {
                        int resume;
                        if (TrySkipString(snapshot, index + hashes, hashes, limit, out resume)) return true;
                        index = resume;
                        continue;
                    }
                    index += Math.Max(hashes, 1);
                    continue;
                }
                if (current == '"' || current == '\'')
                {
                    int resume;
                    if (TrySkipString(snapshot, index, 0, limit, out resume)) return true;
                    index = resume;
                    continue;
                }
                index++;
            }
            return false;
        }

        private static bool TrySkipString(ITextSnapshot snapshot, int quoteIndex, int rawLevel, int limit, out int resume)
        {
            int length = snapshot.Length;
            bool triple = snapshot[quoteIndex] == '"' && quoteIndex + 2 < length && snapshot[quoteIndex + 1] == '"' && snapshot[quoteIndex + 2] == '"';
            resume = quoteIndex + 1;
            if (triple) return TrySkipTripleString(snapshot, quoteIndex + 3, rawLevel, limit, out resume);
            return TrySkipSingleLineString(snapshot, quoteIndex, rawLevel, limit, out resume);
        }

        private static bool TrySkipSingleLineString(ITextSnapshot snapshot, int quoteIndex, int rawLevel, int limit, out int resume)
        {
            int length = snapshot.Length;
            char quote = snapshot[quoteIndex];
            int index = quoteIndex + 1;
            while (index < limit)
            {
                char current = snapshot[index];
                if (current == '\\')
                {
                    if (rawLevel == 0)
                    {
                        index += index + 1 < length ? 2 : 1;
                        continue;
                    }
                    int hashes = CountHashes(snapshot, index + 1, length);
                    index += hashes == rawLevel && index + 1 + rawLevel < length ? 1 + rawLevel + 1 : 1;
                    continue;
                }
                if (current == '\n' || current == '\r')
                {
                    resume = index;
                    return false;
                }
                if (current != quote)
                {
                    index++;
                    continue;
                }
                if (rawLevel == 0 || CountHashes(snapshot, index + 1, length) >= rawLevel)
                {
                    resume = index + 1 + rawLevel;
                    return false;
                }
                index++;
            }
            resume = index;
            return true;
        }

        private static bool TrySkipTripleString(ITextSnapshot snapshot, int contentStart, int rawLevel, int limit, out int resume)
        {
            int length = snapshot.Length;
            int index = contentStart;
            int bound = Math.Max(limit, contentStart);
            while (index < bound)
            {
                char current = snapshot[index];
                if (current == '\\')
                {
                    if (rawLevel == 0)
                    {
                        index += index + 1 < length ? 2 : 1;
                        continue;
                    }
                    int hashes = CountHashes(snapshot, index + 1, length);
                    index += hashes == rawLevel && index + 1 + rawLevel < length ? 1 + rawLevel + 1 : 1;
                    continue;
                }
                if (current == '"' && index + 2 < length && snapshot[index + 1] == '"' && snapshot[index + 2] == '"' && CountHashes(snapshot, index + 3, length) >= rawLevel)
                {
                    resume = index + 3 + rawLevel;
                    return false;
                }
                index++;
            }
            resume = index;
            return true;
        }

        private static bool TrySkipBlockComment(ITextSnapshot snapshot, int start, int limit, out int end)
        {
            int depth = 1;
            int index = start;
            while (index + 1 < limit)
            {
                char current = snapshot[index];
                char next = snapshot[index + 1];
                if (current == '/' && next == '*')
                {
                    depth++;
                    index += 2;
                    continue;
                }
                if (current == '*' && next == '/')
                {
                    index += 2;
                    depth--;
                    if (depth == 0)
                    {
                        end = index;
                        return true;
                    }
                    continue;
                }
                index++;
            }
            end = index;
            return false;
        }

        private static int IndexOfLineEnd(ITextSnapshot snapshot, int start, int limit)
        {
            int index = start;
            while (index < limit)
            {
                char current = snapshot[index];
                if (current == '\n' || current == '\r') return index;
                index++;
            }
            return limit;
        }

        private static int CountHashes(ITextSnapshot snapshot, int start, int bound)
        {
            int count = 0;
            while (start + count < bound && snapshot[start + count] == '#') count++;
            return count;
        }
    }
}
