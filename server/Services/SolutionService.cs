using System.Text.RegularExpressions;
using System.Xml.Linq;
using Stackwright.Server.Models;

namespace Stackwright.Server.Services;

/// <summary>Reads a whole solution: its projects, what each one is for, how they depend on each other, and their endpoints.</summary>
public static class SolutionService
{
    public static SolutionDetails Load(string solutionPath)
    {
        if (!File.Exists(solutionPath)) throw new StudioException("The solution file could not be found. It may have been moved or deleted.");
        var full = Path.GetFullPath(solutionPath);
        var dir = Path.GetDirectoryName(full)!;
        var projectPaths = ProjectPaths(full, dir);

        var infos = projectPaths.AsParallel().AsOrdered().Select(path =>
        {
            var name = Path.GetFileNameWithoutExtension(path);
            ProjectFile pf;
            try { pf = ProjectFile.Load(path); }
            catch { return new SolutionProjectInfo(name, path, null, "Library", false, [], null, 0); }

            var refs = pf.Document.Descendants().Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => Path.GetFileNameWithoutExtension(((string?)e.Attribute("Include") ?? "").Replace('\\', '/')))
                .Where(n => n.Length > 0).ToList();
            var tfm = pf.TargetFramework;
            var (_, _, label) = ProjectAnalyzer.DescribeFramework(tfm);
            var sources = SourceTexts(Path.GetDirectoryName(path)!);
            var role = ProjectAnalyzer.DetectRole(name, pf, sources);

            int? endpoints = null;
            if (pf.IsWeb || role == "Controllers")
            {
                try { endpoints = ProjectAnalyzer.Analyze(path).Endpoints.Count(e => !e.File.IsReference || role == "Controllers"); }
                catch { /* unreadable project: show it without a count */ }
            }
            return new SolutionProjectInfo(name, path, tfm.Length > 0 ? label : null, role, pf.IsWeb, refs, endpoints, sources.Count);
        }).ToList();

        var order = new[] { "API", "Controllers", "Services", "Data access", "Models", "Library", "Tests" };
        infos = infos.OrderBy(p => Array.IndexOf(order, p.Role)).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new SolutionDetails(Path.GetFileNameWithoutExtension(full), full, infos);
    }

    private static List<string> ProjectPaths(string slnPath, string dir)
    {
        var rel = new List<string>();
        if (slnPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                rel.AddRange(XDocument.Load(slnPath).Descendants().Where(e => e.Name.LocalName == "Project")
                    .Select(e => (string?)e.Attribute("Path") ?? ""));
            }
            catch { throw new StudioException("The solution file couldn't be read."); }
        }
        else
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(slnPath), @"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+)""", RegexOptions.Multiline))
                rel.Add(m.Groups[1].Value);
        }
        return rel.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFullPath(Path.Combine(dir, p.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> SourceTexts(string projectDir)
    {
        var list = new List<string>();
        Walk(new DirectoryInfo(projectDir), true);
        return list;

        void Walk(DirectoryInfo d, bool root)
        {
            if (!root && (d.Name is "bin" or "obj" or "node_modules" or ".git" or ".vs" || d.EnumerateFiles("*.csproj").Any())) return;
            foreach (var f in d.EnumerateFiles("*.cs"))
            {
                try { list.Add(File.ReadAllText(f.FullName)); } catch { /* skip */ }
                if (list.Count > 2000) return;
            }
            foreach (var sub in d.EnumerateDirectories()) Walk(sub, false);
        }
    }
}
