using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EndpointStudio.Server.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointStudio.Server.Services;

public class SourceFile
{
    public required string Path { get; init; }
    public required string Relative { get; init; }
    public required string Text { get; init; }
    public required CompilationUnitSyntax Root { get; init; }
}

public class EndpointRecord
{
    public required EndpointInfo Info { get; init; }
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
        Files.FirstOrDefault(f => System.IO.Path.GetFileName(f.Path).Equals("Program.cs", StringComparison.OrdinalIgnoreCase))
        ?? Files.FirstOrDefault(f => f.Text.Contains("WebApplication.CreateBuilder"));
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

        XDocument doc;
        try { doc = XDocument.Load(csprojPath); }
        catch { throw new StudioException("The project file is damaged or not a valid .NET project."); }

        var props = doc.Descendants().Where(e => e.Parent?.Name.LocalName == "PropertyGroup").ToList();
        string? Prop(string n) => props.FirstOrDefault(e => e.Name.LocalName == n)?.Value.Trim();

        var sdk = (string?)doc.Root?.Attribute("Sdk") ?? "";
        var legacy = sdk.Length == 0 && doc.Root?.Elements().All(e => e.Name.LocalName != "Sdk") == true;
        var tfm = Prop("TargetFramework") ?? Prop("TargetFrameworks")?.Split(';').FirstOrDefault()
                  ?? (Prop("TargetFrameworkVersion") is { } tfv ? "net" + tfv.TrimStart('v').Replace(".", "") : "unknown");
        var (platform, major, label) = DescribeFramework(tfm);
        var rootNs = Prop("RootNamespace") ?? Regex.Replace(name, @"[^A-Za-z0-9_.]", "_");
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
        var projectTypes = Prop("ProjectTypeGuids") ?? "";

        var model = new ProjectModel
        {
            CsprojPath = Path.GetFullPath(csprojPath),
            Directory = dir,
            Name = name,
            TargetFramework = tfm,
            RootNamespace = rootNs,
            Packages = packages,
            Files = LoadSources(dir),
            Platform = platform,
            FrameworkVersion = major,
            FrameworkLabel = label,
            NullableEnabled = string.Equals(Prop("Nullable"), "enable", StringComparison.OrdinalIgnoreCase),
            ImplicitUsings = string.Equals(Prop("ImplicitUsings"), "enable", StringComparison.OrdinalIgnoreCase) || string.Equals(Prop("ImplicitUsings"), "true", StringComparison.OrdinalIgnoreCase),
            IsLegacyProject = legacy,
            IsWebProject = sdk.Contains("Web", StringComparison.OrdinalIgnoreCase)
                           || doc.Descendants().Any(e => e.Name.LocalName == "FrameworkReference" && ((string?)e.Attribute("Include"))?.Contains("AspNetCore") == true)
                           || projectTypes.Contains("349C5851-65DF-11DA-9384-00065B846F21", StringComparison.OrdinalIgnoreCase)
                           || references.Contains("System.Web.Http") || packages.Any(p => p.StartsWith("Microsoft.AspNet.WebApi", StringComparison.OrdinalIgnoreCase))
                           || packages.Any(p => p.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase)),
        };

        if (!model.IsWebProject)
            model.Warnings.Add("This doesn't look like a web API project, so it may not be able to serve endpoints.");

        foreach (var file in model.Files)
        {
            FindControllers(model, file);
            FindMinimalApis(model, file);
        }
        FindEntities(model);
        return model;
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
            var doc = XDocument.Load(csprojPath);
            string? Prop(string n) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == n && e.Parent?.Name.LocalName == "PropertyGroup")?.Value.Trim();
            var legacy = doc.Root?.Attribute("Sdk") is null && doc.Root?.Elements().All(e => e.Name.LocalName != "Sdk") == true;
            var tfm = Prop("TargetFramework") ?? Prop("TargetFrameworks")?.Split(';').FirstOrDefault()
                      ?? (Prop("TargetFrameworkVersion") is { } tfv ? "net" + tfv.TrimStart('v').Replace(".", "") : "");
            var (p, major, _) = DescribeFramework(tfm);
            return (p, major, legacy);
        }
        catch { return (ProjectPlatform.NetCore, 0, false); }
    }

    private static List<SourceFile> LoadSources(string projectDir)
    {
        var files = new List<SourceFile>();
        Walk(new DirectoryInfo(projectDir), true);
        return files;

        void Walk(DirectoryInfo d, bool isRoot)
        {
            if (!isRoot)
            {
                var n = d.Name;
                if (n.Equals("bin", StringComparison.OrdinalIgnoreCase) || n.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith('.') || n.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) return;
                if (d.EnumerateFiles("*.csproj").Any()) return; // another project
            }
            foreach (var f in d.EnumerateFiles("*.cs"))
            {
                var text = File.ReadAllText(f.FullName);
                var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, f.FullName);
                files.Add(new SourceFile
                {
                    Path = f.FullName,
                    Relative = Path.GetRelativePath(projectDir, f.FullName).Replace('\\', '/'),
                    Text = text,
                    Root = (CompilationUnitSyntax)tree.GetRoot(),
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
            var classAuth = HasAttribute(cls.AttributeLists, "Authorize");

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

                var requiresAuth = (classAuth || HasAttribute(m.AttributeLists, "Authorize")) && !HasAttribute(m.AttributeLists, "AllowAnonymous");
                var summary = GetDocSummary(m) ?? GetAttributes(m.AttributeLists, "EndpointSummary").Select(FirstStringArg).FirstOrDefault();

                var info = new EndpointInfo(
                    Id: MakeId(file, m.Identifier.SpanStart),
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
