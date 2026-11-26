using System.Text.RegularExpressions;
using System.Xml.Linq;
using Stackwright.Server.Models;
using Stackwright.Server.Services.Frontend;

namespace Stackwright.Server.Services;

public class FileSystemService
{
    // Only tool and build-output folders are skipped. Folder names that merely start with a dot
    // (for example ".Net6" or ".Net-core-template") are normal folders and are shown.
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", ".git", ".vs", ".idea", ".vscode", ".svn", ".hg",
        ".github", ".gitlab", ".nuget", ".config", ".angular", ".next", ".cache",
        "$RECYCLE.BIN", "System Volume Information",
    };

    public List<DriveEntry> GetDrives() =>
        DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new DriveEntry(d.Name.TrimEnd('\\'), d.RootDirectory.FullName, SafeLabel(d)))
            .ToList();

    private static string? SafeLabel(DriveInfo d)
    {
        try { return string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel; } catch { return null; }
    }

    private static bool ShouldSkip(DirectoryInfo d)
    {
        if (SkipFolders.Contains(d.Name)) return true;
        try { return (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0; }
        catch { return true; }
    }

    public BrowseResult Browse(string? path, string? extensions = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var drives = GetDrives().Select(d => new FolderEntry(d.Label is null ? d.Name : $"{d.Name} ({d.Label})", d.Path)).ToList();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            drives.Insert(0, new FolderEntry("Home", home));
            return new BrowseResult(null, null, drives, [], []);
        }

        var dir = new DirectoryInfo(path);
        if (!dir.Exists) throw new StudioException("That folder doesn't exist anymore.");

        var folders = new List<FolderEntry>();
        try
        {
            foreach (var d in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (ShouldSkip(d)) continue;
                folders.Add(new FolderEntry(d.Name, d.FullName));
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new StudioException("You don't have permission to open this folder.");
        }

        var projects = dir.EnumerateFiles("*.csproj").Select(f => Describe(f.FullName)).ToList();
        // Angular / React / Next.js / Vue apps: this folder itself, and the folders directly inside it
        if (DescribeFrontend(dir.FullName) is { } self) projects.Add(self);
        foreach (var f in folders.Take(300))
            if (DescribeFrontend(f.Path) is { } fe) projects.Add(fe);
        var solutions = dir.EnumerateFiles("*.sln").Concat(dir.EnumerateFiles("*.slnx"))
            .Select(f => new SolutionEntry(Path.GetFileNameWithoutExtension(f.Name), f.FullName))
            .ToList();

        List<FileEntry>? files = null;
        if (!string.IsNullOrWhiteSpace(extensions))
        {
            var exts = extensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.StartsWith('.') ? e : "." + e).ToHashSet(StringComparer.OrdinalIgnoreCase);
            files = dir.EnumerateFiles().Where(f => exts.Contains(f.Extension))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new FileEntry(f.Name, f.FullName, f.Length)).ToList();
        }
        return new BrowseResult(dir.FullName, dir.Parent?.FullName, folders, projects, solutions, files);
    }

    /// <summary>Finds .csproj files below a folder.</summary>
    public List<ProjectFileEntry> FindProjects(string root, int maxDepth = 7)
    {
        var result = new List<ProjectFileEntry>();
        if (!Directory.Exists(root)) throw new StudioException("That folder doesn't exist.");
        Walk(new DirectoryInfo(root), 0);
        return result.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();

        void Walk(DirectoryInfo d, int depth)
        {
            if (result.Count >= 500) return;
            try
            {
                foreach (var f in d.EnumerateFiles("*.csproj"))
                    result.Add(Describe(f.FullName));
                if (DescribeFrontend(d.FullName) is { } fe) { result.Add(fe); return; } // don't look inside a frontend app
                if (depth >= maxDepth) return;
                foreach (var sub in d.EnumerateDirectories())
                {
                    if (ShouldSkip(sub)) continue;
                    Walk(sub, depth + 1);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
    }

    /// <summary>Lists the C# projects that belong to a solution (.sln or .slnx).</summary>
    public List<ProjectFileEntry> SolutionProjects(string solutionPath)
    {
        if (!File.Exists(solutionPath)) throw new StudioException("That solution file doesn't exist anymore.");
        var dir = Path.GetDirectoryName(solutionPath)!;
        var relPaths = new List<string>();
        if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                relPaths.AddRange(XDocument.Load(solutionPath).Descendants()
                    .Where(e => e.Name.LocalName == "Project")
                    .Select(e => (string?)e.Attribute("Path") ?? ""));
            }
            catch { throw new StudioException("The solution file couldn't be read."); }
        }
        else
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(solutionPath), @"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+)""", RegexOptions.Multiline))
                relPaths.Add(m.Groups[1].Value);
        }

        return relPaths
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFullPath(Path.Combine(dir, p.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Describe)
            .OrderByDescending(p => p.IsWeb)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>An Angular / React / Next.js / Vue app in this folder, if there is one.</summary>
    public static ProjectFileEntry? DescribeFrontend(string dir)
    {
        var kind = FrontendAnalyzer.Detect(dir);
        if (kind is null) return null;
        var pkg = Path.Combine(dir, "package.json");
        string? version = null;
        try
        {
            var core = kind switch { "Angular" => "@angular/core", "Next.js" => "next", "Vue" => "vue", _ => "react" };
            var raw = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(pkg))?["dependencies"]?[core]?.ToString();
            version = raw is null ? null : System.Text.RegularExpressions.Regex.Match(raw, @"\d+").Value;
        }
        catch { /* version is optional */ }
        return new ProjectFileEntry(Path.GetFileName(dir), pkg, string.IsNullOrEmpty(version) ? kind : $"{kind} {version}", true, "frontend");
    }

    /// <summary>Reads just enough of a project file to show its name, .NET version and whether it is a web project.</summary>
    private static ProjectFileEntry Describe(string csprojPath)
    {
        var name = Path.GetFileNameWithoutExtension(csprojPath);
        try
        {
            var pf = ProjectFile.Load(csprojPath);
            var tfm = pf.TargetFramework;
            var (_, _, label) = ProjectAnalyzer.DescribeFramework(tfm);
            return new ProjectFileEntry(name, csprojPath, string.IsNullOrEmpty(tfm) ? null : label, pf.IsWeb);
        }
        catch
        {
            return new ProjectFileEntry(name, csprojPath, null, false);
        }
    }
}
