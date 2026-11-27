using System.Text;
using System.Text.RegularExpressions;
using QosriStudio.Server.Models;

namespace QosriStudio.Server.Services;

public record ProjectFileItem(string Name, string Path, bool IsDir, long Size, DateTime Modified, int? Children);
public record FileContent(string Path, string Text, bool Binary, bool TooLarge, string Encoding, string LineEnding, long Size, DateTime Modified, bool ReadOnly);
public record FileSearchHit(string Path, int Line, string Text);
public record FileWriteBody(string Root, string Path, string Text, DateTime? Expected);
public record FileCreateBody(string Root, string Path, bool IsDir, string? Text);
public record FileRenameBody(string Root, string Path, string NewName);
public record FileDeleteBody(string Root, string Path);

/// <summary>Lets people browse and edit the files of an open project, solution or app. Everything stays inside its folder.</summary>
public class ProjectFilesService(HistoryService history)
{
    private const long MaxEditable = 4 * 1024 * 1024;
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".angular", ".next", ".nuxt", ".svelte-kit", ".turbo", ".cache", "packages", "TestResults", ".nx",
    };
    private static readonly HashSet<string> BinaryExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".webp", ".bmp", ".zip", ".7z", ".gz", ".tar", ".pdf", ".woff", ".woff2", ".ttf", ".eot", ".otf",
        ".mp3", ".mp4", ".mov", ".avi", ".db", ".sqlite", ".mdf", ".ldf", ".bak", ".nupkg", ".snk", ".pfx", ".p12", ".cer", ".jar", ".class", ".so", ".dylib", ".bin",
    };

    /// <summary>The folder a project, solution or app lives in.</summary>
    public static string RootOf(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full)) return Path.GetDirectoryName(full)!;
        if (Directory.Exists(full)) return full;
        throw new StudioException("That folder no longer exists.");
    }

    private static string Resolve(string root, string rel)
    {
        var dir = RootOf(root);
        var full = Path.GetFullPath(Path.Combine(dir, (rel ?? "").Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar)));
        var withSep = dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
        if (!full.Equals(dir, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(withSep, StringComparison.OrdinalIgnoreCase))
            throw new StudioException("Only files inside the project folder can be opened.");
        return full;
    }

    private static string Rel(string root, string full) => Path.GetRelativePath(RootOf(root), full).Replace(Path.DirectorySeparatorChar, '/');

    public List<ProjectFileItem> List(string root, string? dir, bool showHidden)
    {
        var full = Resolve(root, dir ?? "");
        if (!Directory.Exists(full)) throw new StudioException("That folder no longer exists.");
        var info = new DirectoryInfo(full);
        var dirs = info.EnumerateDirectories()
            .Where(d => showHidden || !Hidden.Contains(d.Name) && !d.Attributes.HasFlag(FileAttributes.Hidden))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new ProjectFileItem(d.Name, Rel(root, d.FullName), true, 0, d.LastWriteTimeUtc, SafeCount(d)));
        var files = info.EnumerateFiles()
            .Where(f => showHidden || !f.Attributes.HasFlag(FileAttributes.Hidden))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new ProjectFileItem(f.Name, Rel(root, f.FullName), false, f.Length, f.LastWriteTimeUtc, null));
        return dirs.Concat(files).ToList();
    }

    private static int? SafeCount(DirectoryInfo d)
    {
        try { return d.EnumerateFileSystemInfos().Take(1000).Count(); } catch { return null; }
    }

    public FileContent Read(string root, string rel)
    {
        var full = Resolve(root, rel);
        if (!File.Exists(full)) throw new StudioException("That file no longer exists.");
        var fi = new FileInfo(full);
        var readOnly = fi.IsReadOnly;
        if (BinaryExt.Contains(fi.Extension)) return new FileContent(rel, "", true, false, "", "", fi.Length, fi.LastWriteTimeUtc, readOnly);
        if (fi.Length > MaxEditable) return new FileContent(rel, "", false, true, "", "", fi.Length, fi.LastWriteTimeUtc, readOnly);
        var bytes = File.ReadAllBytes(full);
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).IndexOf((byte)0) >= 0 && !HasUtf16Bom(bytes))
            return new FileContent(rel, "", true, false, "", "", fi.Length, fi.LastWriteTimeUtc, readOnly);
        var (text, enc) = Decode(bytes);
        var eol = text.Contains("\r\n") ? "CRLF" : text.Contains('\n') ? "LF" : Environment.NewLine == "\r\n" ? "CRLF" : "LF";
        return new FileContent(rel, text.Replace("\r\n", "\n"), false, false, enc, eol, fi.Length, fi.LastWriteTimeUtc, readOnly);
    }

    private static bool HasUtf16Bom(byte[] b) => b.Length >= 2 && (b[0] == 0xFF && b[1] == 0xFE || b[0] == 0xFE && b[1] == 0xFF);

    private static (string Text, string Encoding) Decode(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (Encoding.UTF8.GetString(b, 3, b.Length - 3), "utf-8-bom");
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (Encoding.Unicode.GetString(b, 2, b.Length - 2), "utf-16le");
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2), "utf-16be");
        try { return (new UTF8Encoding(false, true).GetString(b), "utf-8"); }
        catch (DecoderFallbackException) { return (Encoding.Latin1.GetString(b), "latin1"); }
    }

    private static byte[] Encode(string text, string encoding) => encoding switch
    {
        "utf-8-bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
        "utf-16le" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
        "utf-16be" => [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text)],
        "latin1" => Encoding.Latin1.GetBytes(text),
        _ => Encoding.UTF8.GetBytes(text),
    };

    /// <summary>Saves a file the way it was: same encoding and line endings. Recorded in History so it can be undone.</summary>
    public FileContent Write(FileWriteBody b)
    {
        var full = Resolve(b.Root, b.Path);
        var exists = File.Exists(full);
        var encoding = "utf-8";
        var eol = "\n";
        if (exists)
        {
            var current = Read(b.Root, b.Path);
            if (current.Binary || current.TooLarge) throw new StudioException("This file can't be edited here.");
            if (current.ReadOnly) throw new StudioException("This file is read-only.");
            if (b.Expected is { } expected && Math.Abs((current.Modified - expected).TotalSeconds) > 1)
                throw new StudioException("The file was changed outside QosriStudio since you opened it. Reload it first so nothing is lost.");
            encoding = current.Encoding;
            eol = current.LineEnding == "CRLF" ? "\r\n" : "\n";
        }
        else if (OperatingSystem.IsWindows() && Regex.IsMatch(full, @"\.(cs|csproj|sln|props|targets|config|cshtml|razor|resx)$", RegexOptions.IgnoreCase)) eol = "\r\n";
        var text = b.Text.Replace("\r\n", "\n");
        if (eol == "\r\n") text = text.Replace("\n", "\r\n");
        var changes = history.Begin(b.Root, $"Edited {b.Path}");
        changes.WriteBytes(full, Encode(text, encoding));
        changes.Commit();
        return Read(b.Root, b.Path);
    }

    public ProjectFileItem Create(FileCreateBody b)
    {
        var name = (b.Path ?? "").Trim().Replace('\\', '/');
        if (name.Length == 0 || name.Split('/').Any(p => p is "" or "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new StudioException("That isn't a valid name.");
        var full = Resolve(b.Root, name);
        if (File.Exists(full) || Directory.Exists(full)) throw new StudioException("Something with that name already exists.");
        if (b.IsDir) Directory.CreateDirectory(full);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var changes = history.Begin(b.Root, $"Created {name}");
            changes.WriteBytes(full, Encoding.UTF8.GetBytes(b.Text ?? ""));
            changes.Commit();
        }
        var fi = new FileInfo(full);
        return new ProjectFileItem(Path.GetFileName(full), Rel(b.Root, full), b.IsDir, b.IsDir ? 0 : fi.Length, DateTime.UtcNow, b.IsDir ? 0 : null);
    }

    public ProjectFileItem Rename(FileRenameBody b)
    {
        var full = Resolve(b.Root, b.Path);
        if (full.Equals(RootOf(b.Root), StringComparison.OrdinalIgnoreCase)) throw new StudioException("The project folder itself can't be renamed here.");
        var name = (b.NewName ?? "").Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..") throw new StudioException("That isn't a valid name.");
        var target = Resolve(b.Root, Path.Combine(Path.GetDirectoryName(b.Path) ?? "", name));
        if (File.Exists(target) || Directory.Exists(target) && !target.Equals(full, StringComparison.OrdinalIgnoreCase)) throw new StudioException("Something with that name already exists.");
        if (Directory.Exists(full)) Directory.Move(full, target);
        else if (File.Exists(full)) File.Move(full, target);
        else throw new StudioException("It no longer exists.");
        return new ProjectFileItem(name, Rel(b.Root, target), Directory.Exists(target), File.Exists(target) ? new FileInfo(target).Length : 0, DateTime.UtcNow, null);
    }

    /// <summary>Deletes by moving to the Recycle Bin (Windows), so it can be brought back.</summary>
    public string Delete(FileDeleteBody b)
    {
        var full = Resolve(b.Root, b.Path);
        if (full.Equals(RootOf(b.Root), StringComparison.OrdinalIgnoreCase)) throw new StudioException("The project folder itself can't be deleted here.");
        var isDir = Directory.Exists(full);
        if (!isDir && !File.Exists(full)) throw new StudioException("It no longer exists.");
        if (OperatingSystem.IsWindows())
        {
            if (isDir) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(full, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(full, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return "Moved to the Recycle Bin.";
        }
        if (isDir) Directory.Delete(full, true); else File.Delete(full);
        return "Deleted.";
    }

    /// <summary>Finds files by name and lines by content.</summary>
    public object Search(string root, string query, bool content)
    {
        var dir = RootOf(root);
        var q = (query ?? "").Trim();
        if (q.Length < 2) return new { files = Array.Empty<string>(), hits = Array.Empty<FileSearchHit>() };
        var all = Walk(dir).ToList();
        var files = all.Where(f => Path.GetFileName(f).Contains(q, StringComparison.OrdinalIgnoreCase)).Take(100).Select(f => Rel(root, f)).ToList();
        var hits = new List<FileSearchHit>();
        if (content)
        {
            foreach (var f in all)
            {
                if (hits.Count >= 300) break;
                var fi = new FileInfo(f);
                if (BinaryExt.Contains(fi.Extension) || fi.Length > 1024 * 1024) continue;
                string[] lines;
                try { lines = File.ReadAllLines(f); } catch { continue; }
                for (var i = 0; i < lines.Length && hits.Count < 300; i++)
                    if (lines[i].Contains(q, StringComparison.OrdinalIgnoreCase))
                        hits.Add(new FileSearchHit(Rel(root, f), i + 1, lines[i].Trim().Length > 200 ? lines[i].Trim()[..200] : lines[i].Trim()));
            }
        }
        return new { files, hits };
    }

    private static IEnumerable<string> Walk(string dir, int depth = 0)
    {
        if (depth > 12) yield break;
        IEnumerable<string> files, dirs;
        try { files = Directory.EnumerateFiles(dir).ToList(); dirs = Directory.EnumerateDirectories(dir).ToList(); }
        catch { yield break; }
        foreach (var f in files) yield return f;
        foreach (var d in dirs)
        {
            if (Hidden.Contains(Path.GetFileName(d))) continue;
            foreach (var f in Walk(d, depth + 1)) yield return f;
        }
    }
}
