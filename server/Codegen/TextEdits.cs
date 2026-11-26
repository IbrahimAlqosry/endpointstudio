using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using EndpointStudio.Server.Models;
using EndpointStudio.Server.Services;

namespace EndpointStudio.Server.Codegen;

/// <summary>Small helpers for making precise, formatting-preserving edits to C# source text.</summary>
public sealed class TextEdits(string text)
{
    private readonly List<(int Start, int End, string NewText)> _changes = [];
    public string Original { get; } = text;
    public string NewLine { get; } = text.Contains("\r\n") ? "\r\n" : "\n";

    public void Replace(int start, int end, string newText) => _changes.Add((start, end, newText));
    public void Insert(int position, string newText) => _changes.Add((position, position, newText));
    public void Remove(int start, int end) => _changes.Add((start, end, ""));
    public bool HasChanges => _changes.Count > 0;

    public string Apply()
    {
        var ordered = _changes.Select((c, i) => (c, i))
            .OrderBy(x => x.c.Start).ThenBy(x => x.c.End - x.c.Start).ThenBy(x => x.i)
            .Select(x => x.c).ToList();
        var sb = new StringBuilder();
        var pos = 0;
        foreach (var (start, end, newText) in ordered)
        {
            if (start < pos) throw new StudioException("Internal error: overlapping edits. Nothing was changed.");
            sb.Append(Original, pos, start - pos);
            sb.Append(newText);
            pos = end;
        }
        sb.Append(Original, pos, Original.Length - pos);
        return sb.ToString();
    }

    public int LineStart(int position)
    {
        var i = position;
        while (i > 0 && Original[i - 1] != '\n') i--;
        return i;
    }

    public int LineEndIncludingBreak(int position)
    {
        var i = position;
        while (i < Original.Length && Original[i] != '\n') i++;
        return i < Original.Length ? i + 1 : i;
    }

    public string IndentAt(int position)
    {
        var start = LineStart(position);
        var i = start;
        while (i < Original.Length && (Original[i] == ' ' || Original[i] == '\t')) i++;
        return Original[start..i];
    }

    public bool OnlyWhitespaceBefore(int position)
    {
        for (var i = LineStart(position); i < position; i++)
            if (Original[i] != ' ' && Original[i] != '\t') return false;
        return true;
    }

    /// <summary>Adds using directives that are missing from a file.</summary>
    public void AddUsings(CompilationUnitSyntax root, IEnumerable<string> namespaces, string? ownNamespace)
    {
        var existing = root.Usings.Select(u => u.Name?.ToString()).ToHashSet();
        var missing = namespaces.Where(n => !string.IsNullOrEmpty(n) && !existing.Contains(n) && n != ownNamespace)
            .Distinct().OrderBy(n => n).ToList();
        if (missing.Count == 0) return;
        var block = string.Concat(missing.Select(n => $"using {n};{NewLine}"));
        if (root.Usings.Count > 0)
        {
            var last = root.Usings.Last();
            Insert(LineEndIncludingBreak(last.Span.End), block);
        }
        else
        {
            var first = root.GetFirstToken(includeZeroWidth: true);
            Insert(LineStart(first.SpanStart), block + NewLine);
        }
    }

    public static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    public static int CountSyntaxErrors(string code) =>
        CSharpSyntaxTree.ParseText(code, ProjectAnalyzer.ParseOptions).GetDiagnostics()
            .Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

    /// <summary>Makes sure an edit didn't introduce syntax errors.</summary>
    public static void EnsureStillValid(string before, string after)
    {
        if (CountSyntaxErrors(after) > CountSyntaxErrors(before))
            throw new StudioException("This change couldn't be applied safely to the file, so nothing was changed.");
    }
}
