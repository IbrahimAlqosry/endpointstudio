using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Stackwright.Server.Services;

/// <summary>
/// Builds example JSON bodies from the real request classes: source code anywhere in the solution,
/// and classes inside referenced DLLs (read with Roslyn, nothing is executed).
/// </summary>
public sealed class SampleBuilder
{
    private readonly ProjectModel _model;
    private readonly Dictionary<string, ClassDeclarationSyntax> _sourceClasses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, INamedTypeSymbol> _dllTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private readonly bool _camelCase;
    private bool _dllsLoaded;

    public SampleBuilder(ProjectModel model)
    {
        _model = model;
        foreach (var cls in model.Files.SelectMany(f => f.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()).OfType<ClassDeclarationSyntax>())
            _sourceClasses.TryAdd(cls.Identifier.Text, cls);
        foreach (var rec in model.Files.SelectMany(f => f.Root.DescendantNodes().OfType<RecordDeclarationSyntax>()))
            _ = rec; // records are rare in request types; classes cover the common case

        // ASP.NET Core writes camelCase JSON by default; .NET Framework Web API keeps names as written.
        var setup = string.Concat(model.Files.Where(f => !f.IsReference).Select(f => f.Text));
        _camelCase = !model.IsFramework && !setup.Contains("PropertyNamingPolicy = null") && !setup.Contains("DefaultContractResolver");
    }

    public string? For(string type)
    {
        if (_cache.TryGetValue(type, out var hit)) return hit;
        var node = Build(type, new Dictionary<string, string>(), 0);
        var json = node is JsonObject or JsonArray ? node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : null;
        _cache[type] = json;
        return json;
    }

    private JsonNode? Build(string type, Dictionary<string, string> generics, int depth)
    {
        type = type.Trim().TrimEnd('?');
        if (generics.TryGetValue(type, out var mapped)) type = mapped.TrimEnd('?');
        var simple = Simple(type);
        if (simple is not null || depth > 4) return simple;

        var (name, args) = SplitGeneric(type);
        var shortName = name.Split('.').Last();
        if (shortName is "List" or "IList" or "IEnumerable" or "ICollection" or "IReadOnlyList" or "IReadOnlyCollection" or "HashSet" && args.Count == 1)
            return new JsonArray(Build(args[0], generics, depth + 1));
        if (type.EndsWith("[]")) return new JsonArray(Build(type[..^2], generics, depth + 1));
        if (shortName is "Dictionary" or "IDictionary" or "JObject" or "JsonElement" or "JsonObject" or "object" or "dynamic") return new JsonObject();

        if (_sourceClasses.TryGetValue(shortName, out var cls)) return FromSource(cls, args, generics, depth);
        if (FindDllType(shortName, args.Count) is { } symbol) return FromSymbol(symbol, args, generics, depth);
        return new JsonObject();
    }

    private JsonObject FromSource(ClassDeclarationSyntax cls, List<string> args, Dictionary<string, string> outer, int depth)
    {
        var generics = new Dictionary<string, string>(outer);
        if (cls.TypeParameterList is { } tps)
            for (var i = 0; i < tps.Parameters.Count && i < args.Count; i++) generics[tps.Parameters[i].Identifier.Text] = Resolve(args[i], outer);
        var obj = new JsonObject();
        // Base class properties first
        if (cls.BaseList?.Types.FirstOrDefault()?.Type is { } baseType)
        {
            var baseName = ProjectAnalyzer.SimpleName(baseType);
            if (_sourceClasses.TryGetValue(baseName, out var baseCls) && baseCls != cls)
                foreach (var kv in FromSource(baseCls, SplitGeneric(baseType.ToString()).Args, generics, depth).ToList())
                    obj[kv.Key] = kv.Value?.DeepClone();
        }
        foreach (var p in cls.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (!p.Modifiers.Any(SyntaxKind.PublicKeyword) || p.Modifiers.Any(SyntaxKind.StaticKeyword)) continue;
            var attrs = p.AttributeLists.SelectMany(a => a.Attributes).ToList();
            if (attrs.Any(a => ProjectAnalyzer.AttributeName(a) is "JsonIgnore" or "IgnoreDataMember")) continue;
            var jsonName = attrs.Where(a => ProjectAnalyzer.AttributeName(a) is "JsonProperty" or "JsonPropertyName" or "DataMember")
                .Select(ProjectAnalyzer.FirstStringArg).FirstOrDefault(s => !string.IsNullOrEmpty(s));
            obj[jsonName ?? Name(p.Identifier.Text)] = Build(p.Type.ToString(), generics, depth + 1);
        }
        return obj;
    }

    private JsonObject FromSymbol(INamedTypeSymbol symbol, List<string> args, Dictionary<string, string> outer, int depth)
    {
        var generics = new Dictionary<string, string>(outer);
        for (var i = 0; i < symbol.TypeParameters.Length && i < args.Count; i++) generics[symbol.TypeParameters[i].Name] = Resolve(args[i], outer);
        var obj = new JsonObject();
        var chain = new List<INamedTypeSymbol>();
        for (var t = symbol; t is not null && t.SpecialType != SpecialType.System_Object && chain.Count < 5; t = t.BaseType) chain.Insert(0, t);
        foreach (var t in chain)
            foreach (var p in t.GetMembers().OfType<IPropertySymbol>())
            {
                if (p.DeclaredAccessibility != Accessibility.Public || p.IsStatic || p.IsIndexer) continue;
                if (p.GetAttributes().Any(a => a.AttributeClass?.Name is "JsonIgnoreAttribute" or "IgnoreDataMemberAttribute")) continue;
                var jsonName = p.GetAttributes().Where(a => a.AttributeClass?.Name is "JsonPropertyAttribute" or "JsonPropertyNameAttribute")
                    .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string).FirstOrDefault(s => !string.IsNullOrEmpty(s));
                obj[jsonName ?? Name(p.Name)] = Build(p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), generics, depth + 1);
            }
        return obj;
    }

    private static string Resolve(string arg, Dictionary<string, string> generics) =>
        generics.TryGetValue(arg.Trim(), out var v) ? v : arg.Trim();

    private INamedTypeSymbol? FindDllType(string name, int arity)
    {
        LoadDlls();
        return _dllTypes.TryGetValue($"{name}`{arity}", out var t) ? t : _dllTypes.GetValueOrDefault($"{name}`0");
    }

    private void LoadDlls()
    {
        if (_dllsLoaded) return;
        _dllsLoaded = true;
        var dlls = new List<string>();
        foreach (var csproj in new[] { _model.CsprojPath }.Concat(_model.ReferencedProjects.Select(r => r.Path)))
        {
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(csproj);
                foreach (var hint in doc.Descendants().Where(e => e.Name.LocalName == "HintPath"))
                {
                    var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csproj)!, hint.Value.Trim()));
                    if (File.Exists(full) && !dlls.Contains(full, StringComparer.OrdinalIgnoreCase)) dlls.Add(full);
                }
            }
            catch { /* ignore */ }
        }
        if (dlls.Count == 0) return;
        try
        {
            var refs = dlls.Select(d => (MetadataReference)MetadataReference.CreateFromFile(d))
                .Append(MetadataReference.CreateFromFile(typeof(object).Assembly.Location)).ToList();
            var compilation = CSharpCompilation.Create("samples", references: refs);
            foreach (var r in refs.Take(dlls.Count))
                if (compilation.GetAssemblyOrModuleSymbol(r) is IAssemblySymbol asm) Collect(asm.GlobalNamespace);
        }
        catch { /* unreadable DLLs: samples fall back to {} */ }

        void Collect(INamespaceSymbol ns)
        {
            foreach (var t in ns.GetTypeMembers()) _dllTypes.TryAdd($"{t.Name}`{t.Arity}", t);
            foreach (var child in ns.GetNamespaceMembers()) Collect(child);
        }
    }

    private string Name(string n) => _camelCase && n.Length > 0 ? char.ToLowerInvariant(n[0]) + n[1..] : n;

    private static JsonNode? Simple(string t) => t switch
    {
        "string" or "String" or "char" => JsonValue.Create(""),
        "int" or "long" or "short" or "byte" or "uint" or "ulong" or "ushort" or "sbyte" or "Int32" or "Int64" or "Int16" => JsonValue.Create(0),
        "decimal" or "double" or "float" or "Decimal" or "Double" or "Single" => JsonValue.Create(0.0),
        "bool" or "Boolean" => JsonValue.Create(false),
        "DateTime" or "DateTimeOffset" => JsonValue.Create("2026-01-01T00:00:00"),
        "DateOnly" => JsonValue.Create("2026-01-01"),
        "TimeOnly" or "TimeSpan" => JsonValue.Create("00:00:00"),
        "Guid" => JsonValue.Create("00000000-0000-0000-0000-000000000000"),
        _ => null,
    };

    private static (string Name, List<string> Args) SplitGeneric(string type)
    {
        var i = type.IndexOf('<');
        if (i < 0 || !type.EndsWith('>')) return (type, []);
        var inner = type[(i + 1)..^1];
        var args = new List<string>();
        var depth = 0; var start = 0;
        for (var k = 0; k < inner.Length; k++)
        {
            if (inner[k] == '<') depth++;
            else if (inner[k] == '>') depth--;
            else if (inner[k] == ',' && depth == 0) { args.Add(inner[start..k].Trim()); start = k + 1; }
        }
        args.Add(inner[start..].Trim());
        return (type[..i], args);
    }

    /// <summary>Raw C# type of a parameter (EndpointParam.Type is the friendly one), read from the syntax.</summary>
    public static string? RawBodyType(EndpointRecord rec)
    {
        var parameters = rec.Method?.ParameterList.Parameters
                         ?? (rec.MapCall?.ArgumentList.Arguments.LastOrDefault()?.Expression as ParenthesizedLambdaExpressionSyntax)?.ParameterList.Parameters;
        if (parameters is null) return null;
        var body = rec.Info.Parameters.FirstOrDefault(p => p.Source == Models.ParamSource.Body);
        return body is null ? null : parameters.Value.FirstOrDefault(p => p.Identifier.Text == body.Name)?.Type?.ToString();
    }

    public static bool LooksLikeJsonType(string type) => !Regex.IsMatch(type, @"^(string|int|long|bool|decimal|double)\??$");
}
