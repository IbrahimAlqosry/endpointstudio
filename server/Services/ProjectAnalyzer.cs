using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Stackwright.Server.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Stackwright.Server.Services;

public class SourceFile
{
    public required string Path { get; init; }
    public required string Relative { get; init; }
    public required string Text { get; init; }
    public required CompilationUnitSyntax Root { get; init; }
    /// <summary>Name of the project the file belongs to.</summary>
    public string Project { get; init; } = "";
    /// <summary>True for files that come from a referenced project (another project in the solution).</summary>
    public bool IsReference { get; init; }
}

public class EndpointRecord
{
    public required EndpointInfo Info { get; set; }
    public required SourceFile File { get; init; }
    public required GroupRecord Group { get; init; }
    // Controller endpoints
    public MethodDeclarationSyntax? Method { get; init; }
    // Minimal API endpoints
    public InvocationExpressionSyntax? MapCall { get; init; }
    public InvocationExpressionSyntax? OuterCall { get; init; }
    public List<(string Name, InvocationExpressionSyntax Call)> Chain { get; init; } = [];
}

public class GroupRecord
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required EndpointStyle Style { get; init; }
    public required string BaseRoute { get; init; }
    public required SourceFile File { get; init; }
    public ClassDeclarationSyntax? Class { get; init; }
    public string? Receiver { get; init; }
    public bool ClassRequiresAuth { get; init; }
    public List<EndpointRecord> Endpoints { get; } = [];
}

public class ProjectModel
{
    public required string CsprojPath { get; init; }
    public required string Directory { get; init; }
    public required string Name { get; init; }
    public required string TargetFramework { get; init; }
    public required string RootNamespace { get; init; }
    public required List<string> Packages { get; init; }
    public required List<SourceFile> Files { get; init; }
    public List<EndpointRecord> Endpoints { get; } = [];
    public List<GroupRecord> Groups { get; } = [];
    public List<EntityInfo> Entities { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool IsWebProject { get; set; }
    public List<ProjectRefInfo> ReferencedProjects { get; } = [];
    public List<string> BinaryReferences { get; } = [];
    public List<ServiceInfo> Services { get; } = [];
    /// <summary>Package ids of this project and every project it references.</summary>
    public HashSet<string> AllPackages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> SqlProviders { get; } = [];
    public List<string> ConnectionStrings { get; } = [];
    public string? SolutionPath { get; set; }

    /// <summary>.NET Framework (ASP.NET Web API 2) or modern .NET / .NET Core.</summary>
    public required ProjectPlatform Platform { get; init; }
    /// <summary>Major version: 4 for .NET Framework 4.x, 3 for .NET Core 3.1, 8 for .NET 8…</summary>
    public required int FrameworkVersion { get; init; }
    public required string FrameworkLabel { get; init; }
    public bool NullableEnabled { get; init; }
    public bool ImplicitUsings { get; init; }
    /// <summary>Old-style (non-SDK) project file that lists every source file.</summary>
    public bool IsLegacyProject { get; init; }
    /// <summary>Entity Framework 6 (classic) rather than EF Core.</summary>
    public bool UsesEf6 => Packages.Any(p => p.Equals("EntityFramework", StringComparison.OrdinalIgnoreCase));

    public bool IsFramework => Platform == ProjectPlatform.NetFramework;
    // An unknown version (0) is treated as a modern project.
    private bool AtLeast(int major) => !IsFramework && (FrameworkVersion == 0 || FrameworkVersion >= major);
    public bool SupportsMinimalApis => AtLeast(6);
    public bool SupportsMapGroup => AtLeast(7);
    public bool SupportsFileScopedNamespace => AtLeast(6);
    public bool SupportsWithSummary => AtLeast(7);

    public EndpointStyle PreferredStyle =>
        Groups.Any(g => g.Style == EndpointStyle.Controller) ? EndpointStyle.Controller :
        Groups.Any(g => g.Style == EndpointStyle.MinimalApi) ? EndpointStyle.MinimalApi :
        Files.Any(f => f.Text.Contains("MapControllers(")) ? EndpointStyle.Controller : EndpointStyle.MinimalApi;

    public SourceFile? ProgramFile =>
        Files.FirstOrDefault(f => !f.IsReference && System.IO.Path.GetFileName(f.Path).Equals("Program.cs", StringComparison.OrdinalIgnoreCase))
        ?? Files.FirstOrDefault(f => !f.IsReference && f.Text.Contains("WebApplication.CreateBuilder"));
}

public enum ProjectPlatform { NetCore, NetFramework }

public static class ProjectAnalyzer
{
    public static readonly string[] HttpMethods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    private static readonly Dictionary<string, string> MapMethodNames = new()
    {
        ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT", ["MapDelete"] = "DELETE", ["MapPatch"] = "PATCH", ["MapMethods"] = "*",
    };

    private static readonly Dictionary<string, string> HttpAttributeNames = new()
    {
        ["HttpGet"] = "GET", ["HttpPost"] = "POST", ["HttpPut"] = "PUT", ["HttpDelete"] = "DELETE",
        ["HttpPatch"] = "PATCH", ["HttpHead"] = "HEAD", ["HttpOptions"] = "OPTIONS",
    };

    private static readonly HashSet<string> SimpleTypes = new(StringComparer.Ordinal)
    {
        "string", "int", "long", "short", "byte", "bool", "decimal", "double", "float", "Guid", "DateTime",
        "DateTimeOffset", "DateOnly", "TimeOnly", "TimeSpan", "char", "uint", "ulong", "ushort", "sbyte",
        "Int32", "Int64", "String", "Boolean", "Decimal", "Double",
    };

    private static readonly HashSet<string> ServiceTypes = new(StringComparer.Ordinal)
    {
        "CancellationToken", "HttpContext", "HttpRequest", "HttpResponse", "ClaimsPrincipal", "IFormFile", "IFormFileCollection", "Stream", "PipeReader",
    };

    public static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    public static ProjectModel Analyze(string csprojPath)
    {
        if (!File.Exists(csprojPath)) throw new StudioException("The project file could not be found. It may have been moved or deleted.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
        var name = Path.GetFileNameWithoutExtension(csprojPath);

        ProjectFile pf;
        try { pf = ProjectFile.Load(csprojPath); }
        catch { throw new StudioException("The project file is damaged or not a valid .NET project."); }
        var doc = pf.Document;
        string? Prop(string n) => pf.Property(n);

        var tfm = pf.TargetFramework.Length > 0 ? pf.TargetFramework : "unknown";
        var rootNs = Prop("RootNamespace") ?? Regex.Replace(Prop("AssemblyName") ?? name, @"[^A-Za-z0-9_.]", "_");
        var sdk = pf.Sdk;
        var legacy = pf.IsLegacy;
        var (platform, major, label) = DescribeFramework(tfm);
        var packages = doc.Descendants().Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update") ?? "")
            .Where(s => s.Length > 0).ToList();
        var packagesConfig = Path.Combine(dir, "packages.config");
        if (File.Exists(packagesConfig))
        {
            try
            {
                packages.AddRange(XDocument.Load(packagesConfig).Descendants("package")
                    .Select(e => (string?)e.Attribute("id") ?? "").Where(s => s.Length > 0));
            }
            catch { /* ignore a broken packages.config */ }
        }
        var references = doc.Descendants().Where(e => e.Name.LocalName == "Reference")
            .Select(e => ((string?)e.Attribute("Include") ?? "").Split(',')[0].Trim()).ToList();

        var model = new ProjectModel
        {
            CsprojPath = Path.GetFullPath(csprojPath),
            Directory = dir,
            Name = name,
            TargetFramework = tfm,
            RootNamespace = rootNs,
            Packages = packages,
            Files = LoadSources(dir, dir, name, false),
            Platform = platform,
            FrameworkVersion = major,
            FrameworkLabel = label,
            NullableEnabled = string.Equals(Prop("Nullable"), "enable", StringComparison.OrdinalIgnoreCase),
            ImplicitUsings = string.Equals(Prop("ImplicitUsings"), "enable", StringComparison.OrdinalIgnoreCase) || string.Equals(Prop("ImplicitUsings"), "true", StringComparison.OrdinalIgnoreCase),
            IsLegacyProject = legacy,
            IsWebProject = pf.IsWeb || references.Contains("System.Web.Http")
                           || packages.Any(p => p.StartsWith("Microsoft.AspNet.WebApi", StringComparison.OrdinalIgnoreCase)),
        };

        if (!model.IsWebProject)
            model.Warnings.Add("This doesn't look like a web API project, so it may not be able to serve endpoints.");

        // Read the rest of the solution: projects this one references (services, models, data access…)
        LoadReferencedProjects(model, pf, model.CsprojPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { model.CsprojPath }, 0);
        model.BinaryReferences.AddRange(doc.Descendants()
            .Where(e => e.Name.LocalName == "Reference" && e.Elements().Any(c => c.Name.LocalName == "HintPath"))
            .Select(e => ((string?)e.Attribute("Include") ?? "").Split(',')[0].Trim())
            .Where(n => n.Length > 0 && !packages.Any(pk => n.StartsWith(pk, StringComparison.OrdinalIgnoreCase)))
            .Distinct());
        model.SolutionPath = FindSolution(model.CsprojPath);

        foreach (var file in model.Files)
        {
            FindControllers(model, file);
            FindMinimalApis(model, file);
        }
        foreach (var pkg in packages) model.AllPackages.Add(pkg);
        foreach (var r in references) model.AllPackages.Add(r);
        FindEntities(model);
        FindServices(model);
        FindDataAccess(model);
        LinkServiceCalls(model);
        AddBodySamples(model);
        return model;
    }

    /// <summary>Example JSON for endpoints that receive a body, built from the real request classes.</summary>
    private static void AddBodySamples(ProjectModel model)
    {
        var withBody = model.Endpoints.Where(e => e.Info.Parameters.Any(p => p.Source == ParamSource.Body)).ToList();
        if (withBody.Count == 0) return;
        var builder = new SampleBuilder(model);
        foreach (var rec in withBody)
        {
            var raw = SampleBuilder.RawBodyType(rec);
            if (raw is null) continue;
            try { if (builder.For(raw) is { } json) rec.Info = rec.Info with { BodySample = json }; }
            catch { /* a sample is a nice-to-have */ }
        }
    }

    /// <summary>Notes which service operation each endpoint calls (for example IGeneralService.GetMenus).</summary>
    private static void LinkServiceCalls(ProjectModel model)
    {
        var ops = model.Services.SelectMany(s => s.Operations.Select(o => (s.Interface, o.Name)))
            .GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().Interface);
        if (ops.Count == 0) return;
        foreach (var rec in model.Endpoints)
        {
            SyntaxNode? body = rec.Method is not null ? (SyntaxNode?)rec.Method.Body ?? rec.Method.ExpressionBody : rec.OuterCall;
            var call = body?.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(i => i.Expression is MemberAccessExpressionSyntax ma ? ma.Name.Identifier.Text : null)
                .FirstOrDefault(n => n is not null && ops.ContainsKey(n));
            if (call is not null) rec.Info = rec.Info with { Calls = $"{ops[call]}.{call}" };
        }
    }

    /// <summary>Finds endpoints in a piece of code that isn't part of the project yet (used when copying an endpoint).</summary>
    public static List<EndpointRecord> ParseEndpoints(ProjectModel like, string relative, string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text, ParseOptions);
        var file = new SourceFile { Path = Path.Combine(like.Directory, "__copy__.cs"), Relative = relative, Text = text, Root = (CompilationUnitSyntax)tree.GetRoot(), Project = like.Name };
        var temp = new ProjectModel
        {
            CsprojPath = like.CsprojPath, Directory = like.Directory, Name = like.Name, TargetFramework = like.TargetFramework,
            RootNamespace = like.RootNamespace, Packages = like.Packages, Files = [file], Platform = like.Platform,
            FrameworkVersion = like.FrameworkVersion, FrameworkLabel = like.FrameworkLabel, NullableEnabled = like.NullableEnabled,
            ImplicitUsings = like.ImplicitUsings, IsLegacyProject = like.IsLegacyProject,
        };
        FindControllers(temp, file);
        FindMinimalApis(temp, file);
        return temp.Endpoints;
    }

    private static void LoadReferencedProjects(ProjectModel model, ProjectFile pf, string csprojPath, HashSet<string> visited, int depth)
    {
        if (depth > 6) return;
        var baseDir = Path.GetDirectoryName(csprojPath)!;
        foreach (var include in pf.Document.Descendants().Where(e => e.Name.LocalName == "ProjectReference").Select(e => (string?)e.Attribute("Include")))
        {
            if (string.IsNullOrWhiteSpace(include) || include.Contains("$(")) continue;
            var path = Path.GetFullPath(Path.Combine(baseDir, include.Replace('\\', Path.DirectorySeparatorChar)));
            if (!File.Exists(path) || !visited.Add(path)) continue;
            ProjectFile refPf;
            try { refPf = ProjectFile.Load(path); } catch { continue; }
            var refName = Path.GetFileNameWithoutExtension(path);
            var files = LoadSources(Path.GetDirectoryName(path)!, model.Directory, refName, true);
            model.Files.AddRange(files);
            foreach (var pkg in refPf.Document.Descendants().Where(e => e.Name.LocalName == "PackageReference").Select(e => (string?)e.Attribute("Include")))
                if (!string.IsNullOrEmpty(pkg)) model.AllPackages.Add(pkg);
            var (_, _, label) = DescribeFramework(refPf.TargetFramework);
            model.ReferencedProjects.Add(new ProjectRefInfo(refName, path, refPf.TargetFramework.Length > 0 ? label : null,
                DetectRole(refName, refPf, files.Select(f => f.Text)), refPf.IsWeb));
            LoadReferencedProjects(model, refPf, path, visited, depth + 1);
        }
    }

    /// <summary>The solution file (.sln/.slnx) in this folder or above that lists the project.</summary>
    public static string? FindSolution(string csprojPath)
    {
        var fileName = Path.GetFileName(csprojPath);
        var dir = new DirectoryInfo(Path.GetDirectoryName(csprojPath)!);
        for (var i = 0; dir is not null && i < 4; i++, dir = dir.Parent)
        {
            foreach (var sln in dir.EnumerateFiles("*.sln").Concat(dir.EnumerateFiles("*.slnx")))
            {
                try { if (File.ReadAllText(sln.FullName).Contains(fileName, StringComparison.OrdinalIgnoreCase)) return sln.FullName; }
                catch { /* unreadable */ }
            }
        }
        return null;
    }

    /// <summary>Guesses what a project is for from its name, project file and code.</summary>
    public static string DetectRole(string name, ProjectFile pf, IEnumerable<string> sources)
    {
        var doc = pf.Document.ToString();
        if (doc.Contains("Microsoft.NET.Test.Sdk") || doc.Contains("xunit") || doc.Contains("NUnit") || name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)) return "Tests";
        if (pf.IsWeb) return "API";
        var texts = sources.ToList();
        bool Any(params string[] needles) => texts.Any(t => needles.Any(n => t.Contains(n, StringComparison.Ordinal)));
        if (Any(": ControllerBase", ": ApiController", ": Controller")) return "Controllers";
        // The project name says the most about intent; the code decides only when the name doesn't.
        if (Regex.IsMatch(name, @"Model|DTO|Entit|Domain|Contract", RegexOptions.IgnoreCase)) return "Models";
        if (Regex.IsMatch(name, "Service|Business|Application|Logic", RegexOptions.IgnoreCase)) return "Services";
        if (Regex.IsMatch(name, @"Data|DAL|Repository|Persistence|Infrastructure|\.DB\b|DB\.", RegexOptions.IgnoreCase)) return "Data access";
        if (texts.Any(t => Regex.IsMatch(t, @"interface\s+I\w*Service\b"))) return "Services";
        if (Any(": DbContext", "OracleCommand", "SqlCommand", "NpgsqlCommand", "MySqlCommand", "DbSet<")) return "Data access";
        return "Library";
    }

    /// <summary>Service interfaces (IXxxService) and the classes that implement them, across the solution.</summary>
    private static void FindServices(ProjectModel model)
    {
        var interfaces = model.Files
            .SelectMany(f => f.Root.DescendantNodes().OfType<InterfaceDeclarationSyntax>().Select(i => (File: f, Node: i)))
            .Where(x => Regex.IsMatch(x.Node.Identifier.Text, @"^I[A-Z]\w*(Service|Repository|Manager|Provider)$"))
            .ToList();
        var classes = model.Files.SelectMany(f => f.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(c => (File: f, Node: c))).ToList();
        // Which operations endpoints already call (".OperationName(" inside an endpoint)
        var endpointBodies = string.Concat(model.Endpoints.Select(e => e.Method?.ToString() ?? e.OuterCall?.ToString() ?? ""));
        foreach (var (file, iface) in interfaces)
        {
            var name = iface.Identifier.Text;
            var impl = classes.FirstOrDefault(c => c.Node.BaseList?.Types.Any(t => SimpleName(t.Type) == name) == true);
            var ops = iface.Members.OfType<MethodDeclarationSyntax>().Select(md =>
            {
                var ret = md.ReturnType.ToString();
                var isAsync = Regex.IsMatch(ret, @"^(Task|ValueTask)\b");
                return new ServiceOperation(
                    md.Identifier.Text,
                    md.ParameterList.Parameters.Select(p => new ServiceParam(p.Identifier.Text, p.Type?.ToString() ?? "object", FriendlyType(p.Type?.ToString() ?? "object"), IsServiceLike(SimpleTypeName(p.Type?.ToString() ?? "")))).ToList(),
                    ret,
                    FriendlyReturn(md.ReturnType),
                    isAsync,
                    endpointBodies.Contains("." + md.Identifier.Text + "(", StringComparison.Ordinal));
            }).ToList();
            model.Services.Add(new ServiceInfo(name, impl.Node?.Identifier.Text, file.Project, ops.Count, NamespaceOf(iface), ops));
        }
    }

    private static string SimpleTypeName(string type)
    {
        var t = type.Contains('<') ? type[..type.IndexOf('<')] : type;
        return t.Split('.').Last().TrimEnd('?');
    }

    /// <summary>ADO.NET drivers the project (or the projects it uses) can already reach, and its connection strings.</summary>
    private static void FindDataAccess(ProjectModel model)
    {
        var pkgs = model.AllPackages;
        void Add(string provider) { if (!model.SqlProviders.Contains(provider)) model.SqlProviders.Add(provider); }
        bool Has(params string[] ids) => ids.Any(id => pkgs.Contains(id) || model.BinaryReferences.Any(b => b.Equals(id, StringComparison.OrdinalIgnoreCase)));
        if (Has("Oracle.ManagedDataAccess", "Oracle.ManagedDataAccess.Core", "Oracle.EntityFrameworkCore")) Add("Oracle");
        if (Has("Microsoft.Data.SqlClient", "System.Data.SqlClient", "Microsoft.EntityFrameworkCore.SqlServer") || model.IsFramework) Add("SqlServer");
        if (Has("Npgsql", "Npgsql.EntityFrameworkCore.PostgreSQL")) Add("PostgreSql");
        if (Has("MySqlConnector", "MySql.Data", "Pomelo.EntityFrameworkCore.MySql")) Add("MySql");
        if (Has("Microsoft.Data.Sqlite", "Microsoft.EntityFrameworkCore.Sqlite")) Add("Sqlite");

        foreach (var cfg in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            var path = Path.Combine(model.Directory, cfg);
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path),
                    new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("ConnectionStrings", out var cs) && cs.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var p in cs.EnumerateObject())
                        if (!model.ConnectionStrings.Contains(p.Name)) model.ConnectionStrings.Add(p.Name);
            }
            catch { /* unreadable settings file */ }
        }
        var webConfig = Path.Combine(model.Directory, "Web.config");
        if (File.Exists(webConfig))
        {
            try
            {
                foreach (var add in XDocument.Load(webConfig).Descendants("connectionStrings").Descendants("add"))
                    if ((string?)add.Attribute("name") is { } n && !model.ConnectionStrings.Contains(n)) model.ConnectionStrings.Add(n);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>Turns a target framework moniker into a platform, major version and friendly label.</summary>
    public static (ProjectPlatform Platform, int Major, string Label) DescribeFramework(string tfm)
    {
        var t = tfm.Trim().ToLowerInvariant();
        var m = Regex.Match(t, @"^net(\d)(\d)(\d)?$"); // net48, net472, net461
        if (m.Success)
        {
            var label = $".NET Framework {m.Groups[1].Value}.{m.Groups[2].Value}" + (m.Groups[3].Success ? $".{m.Groups[3].Value}" : "");
            return (ProjectPlatform.NetFramework, int.Parse(m.Groups[1].Value), label);
        }
        m = Regex.Match(t, @"^netcoreapp(\d+)\.(\d+)");
        if (m.Success) return (ProjectPlatform.NetCore, int.Parse(m.Groups[1].Value), $".NET Core {m.Groups[1].Value}.{m.Groups[2].Value}");
        m = Regex.Match(t, @"^net(\d+)\.(\d+)");
        if (m.Success)
        {
            var major = int.Parse(m.Groups[1].Value);
            return (ProjectPlatform.NetCore, major, $".NET {major}" + (m.Groups[2].Value != "0" ? $".{m.Groups[2].Value}" : ""));
        }
        if (t.StartsWith("netstandard")) return (ProjectPlatform.NetCore, 0, ".NET Standard");
        return (ProjectPlatform.NetCore, 0, tfm);
    }

    /// <summary>Quick look at a project file (no source parsing) to know which build tools it needs.</summary>
    public static (ProjectPlatform Platform, int Major, bool Legacy) Peek(string csprojPath)
    {
        try
        {
            var pf = ProjectFile.Load(csprojPath);
            var (p, major, _) = DescribeFramework(pf.TargetFramework);
            return (p, major, pf.IsLegacy);
        }
        catch { return (ProjectPlatform.NetCore, 0, false); }
    }

    private static List<SourceFile> LoadSources(string projectDir, string relativeTo, string projectName, bool isReference)
    {
        var files = new List<SourceFile>();
        Walk(new DirectoryInfo(projectDir), true);
        return files;

        void Walk(DirectoryInfo d, bool isRoot)
        {
            if (!isRoot)
            {
                var n = d.Name;
                if (n is "bin" or "obj" or "node_modules" or ".git" or ".vs" or ".idea" or ".vscode" ||
                    n.Equals("bin", StringComparison.OrdinalIgnoreCase) || n.Equals("obj", StringComparison.OrdinalIgnoreCase)) return;
                if (d.EnumerateFiles("*.csproj").Any()) return; // another project
            }
            foreach (var f in d.EnumerateFiles("*.cs"))
            {
                var text = File.ReadAllText(f.FullName);
                var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, f.FullName);
                files.Add(new SourceFile
                {
                    Path = f.FullName,
                    Relative = Path.GetRelativePath(relativeTo, f.FullName).Replace('\\', '/'),
                    Text = text,
                    Root = (CompilationUnitSyntax)tree.GetRoot(),
                    Project = projectName,
                    IsReference = isReference,
                });
            }
            foreach (var sub in d.EnumerateDirectories()) Walk(sub, false);
        }
    }

    // ------------------------------------------------------------------ Controllers

    private static void FindControllers(ProjectModel model, SourceFile file)
    {
        foreach (var cls in file.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (cls.Modifiers.Any(SyntaxKind.AbstractKeyword) || cls.Modifiers.Any(SyntaxKind.StaticKeyword)) continue;
            var baseNames = cls.BaseList?.Types.Select(t => SimpleName(t.Type)).ToList() ?? [];
            var isController = model.IsFramework
                ? baseNames.Any(b => b.EndsWith("ApiController")) // Web API 2 (MVC view controllers are skipped)
                : HasAttribute(cls.AttributeLists, "ApiController") || baseNames.Any(b => b.EndsWith("Controller") || b.EndsWith("ControllerBase"));
            if (!isController) continue;

            var controllerName = cls.Identifier.Text.EndsWith("Controller") && cls.Identifier.Text.Length > 10
                ? cls.Identifier.Text[..^10] : cls.Identifier.Text;
            var classRoute = GetAttributes(cls.AttributeLists, "Route").Concat(GetAttributes(cls.AttributeLists, "RoutePrefix"))
                .Select(FirstStringArg).FirstOrDefault(s => s is not null) ?? "";
            var baseRoute = NormalizeRoute(ReplaceTokens(classRoute, controllerName, null));
            var classAuth = HasAuthAttribute(cls.AttributeLists);

            var group = new GroupRecord
            {
                Key = $"c:{file.Relative}:{cls.Identifier.Text}",
                Name = SplitWords(controllerName),
                Style = EndpointStyle.Controller,
                BaseRoute = baseRoute,
                File = file,
                Class = cls,
                ClassRequiresAuth = classAuth,
            };

            foreach (var m in cls.Members.OfType<MethodDeclarationSyntax>())
            {
                if (!m.Modifiers.Any(SyntaxKind.PublicKeyword) || m.Modifiers.Any(SyntaxKind.StaticKeyword)) continue;
                if (HasAttribute(m.AttributeLists, "NonAction")) continue;

                string? verb = null; string? template = null;
                foreach (var attr in m.AttributeLists.SelectMany(a => a.Attributes))
                {
                    if (HttpAttributeNames.TryGetValue(AttributeName(attr), out var v))
                    {
                        verb ??= v;
                        template ??= FirstStringArg(attr);
                    }
                }
                var methodRoute = GetAttributes(m.AttributeLists, "Route").Select(FirstStringArg).FirstOrDefault(s => s is not null);
                var action = m.Identifier.Text;
                string full;
                if (model.IsFramework && methodRoute is null)
                {
                    // Web API 2 convention routing: api/{controller}/{id}, verb from the attribute or the method name
                    verb ??= HttpMethods.FirstOrDefault(v => action.StartsWith(v, StringComparison.OrdinalIgnoreCase));
                    if (verb is null) continue;
                    var hasId = m.ParameterList.Parameters.Any(p => p.Identifier.Text.Equals("id", StringComparison.OrdinalIgnoreCase));
                    full = NormalizeRoute($"api/{controllerName}" + (hasId ? "/{id}" : ""));
                    var prefix = baseRoute.TrimEnd('/');
                    template = baseRoute == "/" ? full.TrimStart('/')
                        : full.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) ? full[(prefix.Length + 1)..]
                        : "~" + full;
                }
                else
                {
                    if (verb is null && methodRoute is null) continue;
                    verb ??= "ANY";
                    template ??= methodRoute ?? "";
                    if (template.StartsWith('/') || template.StartsWith("~/"))
                        full = NormalizeRoute(ReplaceTokens(template.TrimStart('~'), controllerName, action));
                    else
                        full = CombineRoutes(baseRoute, ReplaceTokens(template, controllerName, action));
                }

                var routeParams = RouteParameterNames(full);
                var parameters = m.ParameterList.Parameters.Select(p => DescribeParameter(p, routeParams)).ToList();

                var requiresAuth = (classAuth || HasAuthAttribute(m.AttributeLists)) && !HasAttribute(m.AttributeLists, "AllowAnonymous");
                var summary = GetDocSummary(m) ?? GetAttributes(m.AttributeLists, "EndpointSummary").Select(FirstStringArg).FirstOrDefault();

                var info = new EndpointInfo(
                    Id: MakeId(file, m.Identifier.SpanStart),
                    Project: file.Project,
                    Style: EndpointStyle.Controller,
                    HttpMethod: verb,
                    Route: full,
                    RelativeRoute: template,
                    GroupKey: group.Key,
                    Name: action,
                    Summary: summary,
                    Parameters: parameters,
                    Returns: FriendlyReturn(m.ReturnType),
                    RequiresAuth: requiresAuth,
                    File: file.Relative,
                    Line: m.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

                var rec = new EndpointRecord { Info = info, File = file, Group = group, Method = m };
                group.Endpoints.Add(rec);
                model.Endpoints.Add(rec);
            }

            // A controller with no route and no endpoints is a shared base class, not a group of endpoints.
            if (group.Endpoints.Count == 0 && classRoute.Length == 0) continue;
            model.Groups.Add(group);
        }
    }

    // ------------------------------------------------------------------ Minimal APIs

    private sealed record GroupResolution(string Prefix, string? Tags, bool RequiresAuth, string ReceiverKey);

    private static void FindMinimalApis(ProjectModel model, SourceFile file)
    {
        var groups = new Dictionary<string, GroupRecord>();
        foreach (var inv in file.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
            if (!MapMethodNames.TryGetValue(ma.Name.Identifier.Text, out var verb)) continue;
            var args = inv.ArgumentList.Arguments;
            if (args.Count < 2 || args[0].Expression is not LiteralExpressionSyntax lit || !lit.IsKind(SyntaxKind.StringLiteralExpression)) continue;

            var template = lit.Token.ValueText;
            var handlerIndex = 1;
            if (verb == "*")
            {
                if (args.Count < 3) continue;
                verb = args[1].Expression.DescendantNodes().OfType<LiteralExpressionSyntax>()
                    .Select(l => l.Token.ValueText.ToUpperInvariant()).FirstOrDefault() ?? "ANY";
                handlerIndex = 2;
            }

            var resolution = ResolveGroup(ma.Expression, 0);
            var groupKey = $"m:{file.Relative}:{resolution.ReceiverKey}:{resolution.Prefix}";
            if (!groups.TryGetValue(groupKey, out var group))
            {
                var prefix = NormalizeRoute(resolution.Prefix);
                var name = resolution.Tags
                           ?? (prefix.Length > 1 ? SplitWords(ToPascal(prefix.Split('/', StringSplitOptions.RemoveEmptyEntries).Last(s => !s.StartsWith('{')))) : null)
                           ?? SplitWords(Path.GetFileNameWithoutExtension(file.Path).Replace("Endpoints", ""));
                if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(file.Path);
                group = new GroupRecord
                {
                    Key = groupKey,
                    Name = name,
                    Style = EndpointStyle.MinimalApi,
                    BaseRoute = prefix,
                    File = file,
                    Receiver = ma.Expression.ToString(),
                    ClassRequiresAuth = resolution.RequiresAuth,
                };
                groups[groupKey] = group;
                model.Groups.Add(group);
            }

            // Walk the fluent chain: app.MapGet(...).WithName(...).RequireAuthorization()
            var chain = new List<(string, InvocationExpressionSyntax)>();
            SyntaxNode current = inv;
            while (current.Parent is MemberAccessExpressionSyntax pm && pm.Expression == current && pm.Parent is InvocationExpressionSyntax pinv)
            {
                chain.Add((pm.Name.Identifier.Text, pinv));
                current = pinv;
            }
            string? ChainArg(string n) => chain.Where(c => c.Item1 == n).Select(c => FirstStringArg(c.Item2.ArgumentList)).FirstOrDefault(s => s is not null);

            var handler = args[handlerIndex].Expression;
            var full = CombineRoutes(group.BaseRoute, template);
            var routeParams = RouteParameterNames(full);
            var parameters = new List<EndpointParam>();
            var returns = "Response";
            var handlerName = (string?)null;
            switch (handler)
            {
                case ParenthesizedLambdaExpressionSyntax pl:
                    parameters = pl.ParameterList.Parameters.Select(p => DescribeParameter(p, routeParams)).ToList();
                    if (pl.ReturnType is not null) returns = FriendlyReturn(pl.ReturnType);
                    break;
                case SimpleLambdaExpressionSyntax sl:
                    parameters = [DescribeParameter(sl.Parameter, routeParams)];
                    break;
                default:
                    handlerName = handler is MemberAccessExpressionSyntax hm ? hm.Name.Identifier.Text : handler.ToString();
                    var method = model.Files.SelectMany(f => f.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                        .FirstOrDefault(md => md.Identifier.Text == handlerName);
                    if (method is not null)
                    {
                        parameters = method.ParameterList.Parameters.Select(p => DescribeParameter(p, routeParams)).ToList();
                        returns = FriendlyReturn(method.ReturnType);
                    }
                    break;
            }

            var requiresAuth = (resolution.RequiresAuth || chain.Any(c => c.Item1 == "RequireAuthorization"))
                               && !chain.Any(c => c.Item1 == "AllowAnonymous");

            var info = new EndpointInfo(
                Id: MakeId(file, ma.Name.SpanStart),
                Project: file.Project,
                Style: EndpointStyle.MinimalApi,
                HttpMethod: verb,
                Route: full,
                RelativeRoute: template,
                GroupKey: group.Key,
                Name: ChainArg("WithName") ?? handlerName ?? "",
                Summary: ChainArg("WithSummary") ?? ChainArg("WithDescription"),
                Parameters: parameters,
                Returns: returns,
                RequiresAuth: requiresAuth,
                File: file.Relative,
                Line: inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

            var rec = new EndpointRecord
            {
                Info = info, File = file, Group = group, MapCall = inv, OuterCall = (InvocationExpressionSyntax)current, Chain = chain,
            };
            group.Endpoints.Add(rec);
            model.Endpoints.Add(rec);
        }
    }

    /// <summary>Works out the route prefix of a minimal-API receiver, following MapGroup calls and variables.</summary>
    private static GroupResolution ResolveGroup(ExpressionSyntax receiver, int depth)
    {
        if (depth > 10) return new GroupResolution("", null, false, receiver.ToString());
        switch (receiver)
        {
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } call:
            {
                var inner = ResolveGroup(ma.Expression, depth + 1);
                var n = ma.Name.Identifier.Text;
                return n switch
                {
                    "MapGroup" => inner with { Prefix = CombineRoutes(inner.Prefix, FirstStringArg(call.ArgumentList) ?? ""), ReceiverKey = receiver.ToString() },
                    "WithTags" => inner with { Tags = FirstStringArg(call.ArgumentList) ?? inner.Tags },
                    "RequireAuthorization" => inner with { RequiresAuth = true },
                    _ => inner,
                };
            }
            case IdentifierNameSyntax id:
            {
                var declarator = id.Ancestors()
                    .SelectMany(a => a.ChildNodes().OfType<LocalDeclarationStatementSyntax>()
                        .Concat(a.ChildNodes().OfType<GlobalStatementSyntax>().Select(g => g.Statement).OfType<LocalDeclarationStatementSyntax>()))
                    .SelectMany(l => l.Declaration.Variables)
                    .Where(v => v.Identifier.Text == id.Identifier.Text && v.SpanStart < id.SpanStart && v.Initializer is not null)
                    .OrderByDescending(v => v.SpanStart)
                    .FirstOrDefault();
                if (declarator?.Initializer?.Value is { } init && init.ToString().Contains("MapGroup"))
                {
                    var r = ResolveGroup(init, depth + 1);
                    return r with { ReceiverKey = id.Identifier.Text };
                }
                return new GroupResolution("", null, false, id.Identifier.Text);
            }
            default:
                return new GroupResolution("", null, false, receiver.ToString());
        }
    }

    // ------------------------------------------------------------------ Entities (EF Core)

    private static void FindEntities(ProjectModel model)
    {
        var classes = model.Files
            .SelectMany(f => f.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            .GroupBy(c => c.Identifier.Text)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var ctx in classes.Values)
        {
            var bases = ctx.BaseList?.Types.Select(t => SimpleName(t.Type)).ToList() ?? [];
            if (!bases.Any(b => b.EndsWith("DbContext"))) continue;
            foreach (var prop in ctx.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (prop.Type is not GenericNameSyntax { Identifier.Text: "DbSet" } g || g.TypeArgumentList.Arguments.Count != 1) continue;
                var entityName = SimpleName(g.TypeArgumentList.Arguments[0]);
                var props = new List<EntityProperty>();
                string entityNs = NamespaceOf(ctx);
                string? keyName = null, keyType = null;
                if (classes.TryGetValue(entityName, out var entityClass))
                {
                    entityNs = NamespaceOf(entityClass);
                    var all = entityClass.Members.OfType<PropertyDeclarationSyntax>()
                        .Where(p => p.Modifiers.Any(SyntaxKind.PublicKeyword) && !p.Modifiers.Any(SyntaxKind.StaticKeyword))
                        .ToList();
                    var keyed = all.Where(p => HasAttribute(p.AttributeLists, "Key")).ToList();
                    var keyProps = keyed.Count > 0 ? keyed
                        : all.Where(p => p.Identifier.Text.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                                         p.Identifier.Text.Equals(entityName + "Id", StringComparison.OrdinalIgnoreCase)).Take(1).ToList();
                    if (keyProps.Count == 1)
                    {
                        keyName = keyProps[0].Identifier.Text;
                        keyType = keyProps[0].Type.ToString().TrimEnd('?');
                    }
                    foreach (var p in all)
                    {
                        if (HasAttribute(p.AttributeLists, "NotMapped")) continue;
                        var t = p.Type.ToString();
                        if (p.Modifiers.Any(SyntaxKind.VirtualKeyword) || t.StartsWith("ICollection") || t.StartsWith("List<")) continue; // navigations
                        props.Add(new EntityProperty(p.Identifier.Text, t.TrimEnd('?'), keyProps.Contains(p), t.EndsWith('?')));
                    }
                }
                model.Entities.Add(new EntityInfo(entityName, entityNs, prop.Identifier.Text, ctx.Identifier.Text, NamespaceOf(ctx), keyName, keyType, props));
            }
        }
    }

    // ------------------------------------------------------------------ Helpers

    public static string MakeId(SourceFile file, int position) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{file.Relative}|{position}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string NamespaceOf(SyntaxNode node) =>
        string.Join(".", node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));

    private static EndpointParam DescribeParameter(ParameterSyntax p, HashSet<string> routeParams)
    {
        var name = p.Identifier.Text;
        var typeText = p.Type?.ToString() ?? "string";
        var optional = p.Default is not null || typeText.EndsWith('?');
        var baseType = typeText.TrimEnd('?');

        ParamSource source;
        if (HasAttribute(p.AttributeLists, "FromRoute")) source = ParamSource.Route;
        else if (HasAttribute(p.AttributeLists, "FromQuery") || HasAttribute(p.AttributeLists, "FromUri")) source = routeParams.Contains(name) ? ParamSource.Route : ParamSource.Query;
        else if (HasAttribute(p.AttributeLists, "FromBody") || HasAttribute(p.AttributeLists, "FromForm")) source = ParamSource.Body;
        else if (HasAttribute(p.AttributeLists, "FromHeader")) source = ParamSource.Header;
        else if (HasAttribute(p.AttributeLists, "FromServices") || HasAttribute(p.AttributeLists, "FromKeyedServices")) source = ParamSource.Service;
        else if (routeParams.Contains(name)) source = ParamSource.Route;
        else if (SimpleTypes.Contains(baseType) || baseType.EndsWith("[]") && SimpleTypes.Contains(baseType[..^2])) source = ParamSource.Query;
        else if (IsServiceLike(baseType)) source = ParamSource.Service;
        else source = ParamSource.Body;

        return new EndpointParam(name, FriendlyType(typeText), source, optional);
    }

    private static bool IsServiceLike(string type)
    {
        var simple = type.Contains('<') ? type[..type.IndexOf('<')] : type;
        simple = simple.Split('.').Last();
        if (ServiceTypes.Contains(simple)) return true;
        if (simple.Length > 1 && simple[0] == 'I' && char.IsUpper(simple[1])) return true;
        return simple.EndsWith("Context") || simple.EndsWith("Service") || simple.EndsWith("Repository") ||
               simple.EndsWith("Logger") || simple.EndsWith("Factory") || simple.EndsWith("Options");
    }

    public static string FriendlyType(string type)
    {
        var t = type.Replace(" ", "");
        var nullable = t.EndsWith('?');
        t = t.TrimEnd('?');
        var friendly = t switch
        {
            "string" or "String" => "Text",
            "int" or "Int32" or "long" or "Int64" or "short" or "byte" => "Whole number",
            "decimal" or "double" or "float" or "Decimal" or "Double" => "Decimal number",
            "bool" or "Boolean" => "Yes / No",
            "Guid" => "Unique ID",
            "DateTime" or "DateTimeOffset" or "DateOnly" => "Date",
            "TimeOnly" or "TimeSpan" => "Time",
            "JsonElement" or "JsonObject" or "JsonNode" or "object" or "dynamic" => "JSON object",
            _ => null,
        };
        if (friendly is not null) return friendly;
        var m = Regex.Match(t, @"^(?:IEnumerable|List|IList|ICollection|IReadOnlyList|IReadOnlyCollection)<(.+)>$");
        if (m.Success) return $"List of {FriendlyType(m.Groups[1].Value)}";
        if (t.EndsWith("[]")) return $"List of {FriendlyType(t[..^2])}";
        return t + (nullable ? "" : "");
    }

    public static string FriendlyReturn(TypeSyntax type)
    {
        var t = type.ToString().Replace(" ", "");
        while (true)
        {
            var m = Regex.Match(t, @"^(?:Task|ValueTask|ActionResult|Results|Ok)<(.+)>$");
            if (!m.Success) break;
            t = m.Groups[1].Value;
            if (t.Contains(',')) return "Response";
        }
        if (t is "void") return "Nothing";
        if (t is "Task" or "ValueTask" or "IActionResult" or "ActionResult" or "IResult" or "IHttpActionResult" or "HttpResponseMessage") return "Response";
        var list = Regex.Match(t, @"^(?:IEnumerable|List|IList|ICollection|IReadOnlyList|IReadOnlyCollection|IQueryable|IAsyncEnumerable)<(.+)>$");
        if (list.Success) return $"List of {list.Groups[1].Value}";
        if (t.EndsWith("[]")) return $"List of {t[..^2]}";
        return FriendlyType(t);
    }

    public static HashSet<string> RouteParameterNames(string route)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(route, @"\{\*{0,2}([A-Za-z_][A-Za-z0-9_]*)[^}]*\}"))
            set.Add(m.Groups[1].Value);
        return set;
    }

    public static string CombineRoutes(string a, string b)
    {
        a = (a ?? "").Trim().Trim('/');
        b = (b ?? "").Trim();
        if (b.StartsWith('/') || b.StartsWith("~/")) return NormalizeRoute(b.TrimStart('~'));
        b = b.Trim('/');
        var combined = string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : $"{a}/{b}";
        return NormalizeRoute(combined);
    }

    public static string NormalizeRoute(string route) => "/" + (route ?? "").Trim().Trim('/');

    private static string ReplaceTokens(string template, string controller, string? action)
    {
        template = Regex.Replace(template, @"\[controller\]", controller, RegexOptions.IgnoreCase);
        if (action is not null) template = Regex.Replace(template, @"\[action\]", action, RegexOptions.IgnoreCase);
        return template;
    }

    public static string SimpleName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => SimpleName(q.Right),
        GenericNameSyntax g => g.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        AliasQualifiedNameSyntax a => a.Name.Identifier.Text,
        _ => type.ToString(),
    };

    public static string AttributeName(AttributeSyntax a)
    {
        var n = a.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            IdentifierNameSyntax i => i.Identifier.Text,
            GenericNameSyntax g => g.Identifier.Text,
            _ => a.Name.ToString(),
        };
        return n.EndsWith("Attribute") ? n[..^9] : n;
    }

    public static IEnumerable<AttributeSyntax> GetAttributes(SyntaxList<AttributeListSyntax> lists, string name) =>
        lists.SelectMany(l => l.Attributes).Where(a => AttributeName(a) == name);

    public static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string name) => GetAttributes(lists, name).Any();

    /// <summary>[Authorize] or a custom authorization attribute such as [CustomAuthorizationFilter] or [JwtAuthorize].</summary>
    public static bool HasAuthAttribute(SyntaxList<AttributeListSyntax> lists) =>
        lists.SelectMany(l => l.Attributes).Select(AttributeName)
            .Any(n => n == "Authorize" || n.Contains("Authoriz", StringComparison.OrdinalIgnoreCase) && !n.Contains("Anonymous", StringComparison.OrdinalIgnoreCase));

    public static string? FirstStringArg(AttributeSyntax a)
    {
        if (a.ArgumentList is null) return null;
        foreach (var arg in a.ArgumentList.Arguments)
        {
            if (arg.NameEquals is not null && arg.NameEquals.Name.Identifier.Text != "Template") continue;
            if (arg.Expression is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression)) return l.Token.ValueText;
        }
        return null;
    }

    public static string? FirstStringArg(ArgumentListSyntax args) =>
        args.Arguments.Select(a => a.Expression).OfType<LiteralExpressionSyntax>()
            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)).Select(l => l.Token.ValueText).FirstOrDefault();

    private static string? GetDocSummary(SyntaxNode node)
    {
        var doc = node.GetLeadingTrivia()
            .Select(t => t.GetStructure()).OfType<DocumentationCommentTriviaSyntax>().FirstOrDefault();
        var summary = doc?.Content.OfType<XmlElementSyntax>().FirstOrDefault(e => e.StartTag.Name.ToString() == "summary");
        if (summary is null) return null;
        var text = string.Concat(summary.Content.Select(c => c.ToString()));
        text = Regex.Replace(text, @"///", " ");
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return System.Net.WebUtility.HtmlDecode(text);
    }

    public static string SplitWords(string s) => Regex.Replace(s, "(?<=[a-z0-9])(?=[A-Z])", " ").Trim();

    public static string ToPascal(string s)
    {
        var parts = Regex.Split(s, @"[^A-Za-z0-9]+").Where(p => p.Length > 0);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
