using System.Xml.Linq;

namespace Stackwright.Server.Services;

/// <summary>
/// Reads MSBuild properties the way most real solutions define them: in the project file itself, or in a
/// shared Directory.Build.props higher up the folder tree (common for TargetFramework, Nullable, ImplicitUsings…).
/// </summary>
public sealed class ProjectFile
{
    public XDocument Document { get; }
    private readonly List<XDocument> _buildProps = [];

    private ProjectFile(string csprojPath)
    {
        Document = XDocument.Load(csprojPath);
        // Directory.Build.props files from the project folder upwards (nearest first). Projects usually rely on
        // the nearest one; walking further up covers props files that import their parent.
        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(csprojPath))!);
        for (var i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
        {
            var props = Path.Combine(dir.FullName, "Directory.Build.props");
            if (File.Exists(props)) AddProps(props, 0);
        }
    }

    private readonly HashSet<string> _loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads a props file and the files it imports with plain relative paths (e.g. ..\src\Directory.Build.props).</summary>
    private void AddProps(string path, int depth)
    {
        if (depth > 4 || !_loaded.Add(Path.GetFullPath(path))) return;
        XDocument doc;
        try { doc = XDocument.Load(path); } catch { return; }
        _buildProps.Add(doc);
        foreach (var import in doc.Descendants().Where(e => e.Name.LocalName == "Import"))
        {
            var target = (string?)import.Attribute("Project");
            if (string.IsNullOrWhiteSpace(target) || target.Contains("$(")) continue;
            var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, target));
            if (File.Exists(full)) AddProps(full, depth + 1);
        }
    }

    public static ProjectFile Load(string csprojPath) => new(csprojPath);

    /// <summary>The value of a property; the project file wins over Directory.Build.props.</summary>
    public string? Property(string name)
    {
        foreach (var doc in _buildProps.Prepend(Document))
        {
            var v = doc.Descendants().LastOrDefault(e => e.Name.LocalName == name && e.Parent?.Name.LocalName == "PropertyGroup")?.Value.Trim();
            if (!string.IsNullOrEmpty(v) && !v.Contains("$(")) return v;
        }
        return null;
    }

    public string Sdk => (string?)Document.Root?.Attribute("Sdk")
                         ?? Document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Sdk")?.Attribute("Name")?.Value
                         ?? "";

    /// <summary>Old-style (non-SDK) project that lists every file explicitly.</summary>
    public bool IsLegacy => Sdk.Length == 0;

    public string TargetFramework =>
        Property("TargetFramework") ?? Property("TargetFrameworks")?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
        ?? (Property("TargetFrameworkVersion") is { } tfv ? "net" + tfv.TrimStart('v').Replace(".", "") : "");

    /// <summary>A project that can serve HTTP endpoints (web SDK, ASP.NET Core framework reference, or a classic web application).</summary>
    public bool IsWeb
    {
        get
        {
            if (Sdk.Contains("Web", StringComparison.OrdinalIgnoreCase)) return true;
            var items = Document.Descendants().ToList();
            if (items.Any(e => e.Name.LocalName == "FrameworkReference" && ((string?)e.Attribute("Include"))?.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase) == true)) return true;
            var types = Property("ProjectTypeGuids") ?? "";
            if (types.Contains("349C5851-65DF-11DA-9384-00065B846F21", StringComparison.OrdinalIgnoreCase)) return true;
            return IsLegacy && items.Any(e => e.Name.LocalName == "Reference" && ((string?)e.Attribute("Include"))?.StartsWith("System.Web.Http", StringComparison.OrdinalIgnoreCase) == true);
        }
    }
}
