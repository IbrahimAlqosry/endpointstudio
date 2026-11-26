using System.Text;

namespace Stackwright.Server.Services.Frontend;

/// <summary>
/// Small, forgiving helpers for reading TypeScript/JavaScript without a full parser:
/// they understand strings, template literals and comments well enough to find matching brackets.
/// </summary>
public static class SourceScanner
{
    /// <summary>Same-length copy where comments become spaces (and string contents too, unless keepStrings).</summary>
    public static string MaskStringsAndComments(string text, bool keepStrings)
    {
        var sb = new StringBuilder(text);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') sb[i++] = ' ';
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                for (; i < end; i++) if (text[i] != '\n') sb[i] = ' ';
                continue;
            }
            if (c is '"' or '\'' or '`')
            {
                var end = StringEnd(text, i);
                if (!keepStrings) for (var k = i + 1; k < end && k < text.Length; k++) if (text[k] != '\n') sb[k] = ' ';
                i = end + 1;
                continue;
            }
            i++;
        }
        return sb.ToString();
    }

    public static string StripComments(string text) => MaskStringsAndComments(text, keepStrings: true);

    /// <summary>Index of the closing quote of the string starting at <paramref name="start"/>.</summary>
    public static int StringEnd(string text, int start)
    {
        var q = text[start];
        var i = start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\') { i += 2; continue; }
            if (q == '`' && c == '$' && i + 1 < text.Length && text[i + 1] == '{')
            {
                var close = MatchingClose(text, i + 1);
                if (close < 0) return text.Length - 1;
                i = close + 1;
                continue;
            }
            if (c == q) return i;
            if (c == '\n' && q != '`') return i; // unterminated: stop at the line end
            i++;
        }
        return text.Length - 1;
    }

    /// <summary>Index of the bracket that closes the one at <paramref name="open"/> ((, [ or {); -1 if none.</summary>
    public static int MatchingClose(string text, int open)
    {
        if (open < 0 || open >= text.Length) return -1;
        var opener = text[open];
        var closer = opener switch { '(' => ')', '[' => ']', '{' => '}', '<' => '>', _ => '\0' };
        if (closer == '\0') return -1;
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'' or '`') { i = StringEnd(text, i); continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { var nl = text.IndexOf('\n', i); if (nl < 0) return -1; i = nl; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { var e = text.IndexOf("*/", i + 2, StringComparison.Ordinal); if (e < 0) return -1; i = e + 1; continue; }
            if (c == opener) depth++;
            else if (c == closer && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>The unmatched opening bracket that encloses <paramref name="index"/> (text should be comment-masked).</summary>
    public static int EnclosingOpen(string masked, int index, char open, char close)
    {
        var depth = 0;
        for (var i = index - 1; i >= 0; i--)
        {
            var c = masked[i];
            if (c == close) depth++;
            else if (c == open) { if (depth == 0) return i; depth--; }
        }
        return -1;
    }

    /// <summary>Text of an object literal with any nested {…} blocks blanked out (so child routes don't leak into the parent).</summary>
    public static string TopLevelOnly(string text, int open, int close)
    {
        var sb = new StringBuilder(text.Substring(open + 1, close - open - 1));
        var offset = open + 1;
        for (var i = open + 1; i < close; i++)
        {
            var c = text[i];
            if (c is '"' or '\'' or '`') { i = StringEnd(text, i); continue; }
            if (c == '{')
            {
                var end = MatchingClose(text, i);
                if (end < 0 || end >= close) break;
                for (var k = i; k <= end; k++) sb[k - offset] = ' ';
                i = end;
            }
        }
        return sb.ToString();
    }

    /// <summary>Splits on a separator that isn't inside brackets or strings.</summary>
    public static List<string> SplitTopLevel(string text, char separator = ',')
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'' or '`') { i = StringEnd(text, i); continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == separator && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>End ('>') of a JSX tag whose attributes start at <paramref name="from"/>, skipping {…} expressions.</summary>
    public static int TagEnd(string text, int from)
    {
        for (var i = from; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'' or '`') { i = StringEnd(text, i); continue; }
            if (c == '{') { var e = MatchingClose(text, i); if (e < 0) return -1; i = e; continue; }
            if (c == '>') return i;
        }
        return -1;
    }
}
