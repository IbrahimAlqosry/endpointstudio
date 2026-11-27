using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using QosriStudio.Server.Models;

namespace QosriStudio.Server.Services.Contracts;

/// <summary>A type as the API sends it over the wire.</summary>
public sealed record TypeRef(string Kind, string? Name = null, List<TypeRef>? Args = null, TypeRef? Element = null, string? Format = null, bool Nullable = false)
{
    // Kinds: string, integer, number, boolean, array, map, ref, param (generic type parameter), any
    public static TypeRef Any => new("any");
    public TypeRef AsNullable() => this with { Nullable = true };
}

public sealed record PropDef(string JsonName, TypeRef Type, bool Required, string? Description);
public sealed record TypeDef(string Name, List<string> TypeParams, List<PropDef> Props, List<(string Name, long Value)>? Enum, bool StringEnum, string? Description);
public sealed record ParamDef(string Name, string JsonName, TypeRef Type, bool Optional);
public sealed record OperationDef(string Id, string Name, string Tag, string Method, string Path, string? Summary, bool RequiresAuth,
    List<ParamDef> PathParams, List<ParamDef> QueryParams, List<ParamDef> HeaderParams, TypeRef? Body, TypeRef? Response);

public sealed class ApiContract
{
    public required string Title { get; init; }
    public required string Version { get; init; }
    public List<OperationDef> Operations { get; } = [];
    public Dictionary<string, TypeDef> Types { get; } = new(StringComparer.Ordinal);
    public bool CamelCase { get; init; }
    public bool UsesAuth => Operations.Any(o => o.RequiresAuth);

    /// <summary>Builds the contract from a project's source code (and DLLs it references). Nothing is run.</summary>
    public static ApiContract From(ProjectModel model) => new ContractBuilder(model).Build();
}

internal sealed class ContractBuilder
{
    private readonly ProjectModel _model;
    private readonly Dictionary<string, TypeDeclarationSyntax> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnumDeclarationSyntax> _enums = new(StringComparer.Ordinal);
    private readonly ApiContract _contract;
    private readonly bool _stringEnums;
    private readonly HashSet<string> _building = new(StringComparer.Ordinal);

    public ContractBuilder(ProjectModel model)
    {
        _model = model;
        foreach (var f in model.Files)
            foreach (var t in f.Root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (t is EnumDeclarationSyntax e) _enums.TryAdd(e.Identifier.Text, e);
                else if (t is TypeDeclarationSyntax td and (ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax)) _types.TryAdd(td.Identifier.Text, td);
            }
        var setup = string.Concat(model.Files.Where(f => !f.IsReference).Select(f => f.Text));
        var camel = !model.IsFramework && !setup.Contains("PropertyNamingPolicy = null") && !setup.Contains("DefaultContractResolver");
        _stringEnums = setup.Contains("JsonStringEnumConverter") || setup.Contains("StringEnumConverter");
        _contract = new ApiContract { Title = model.Name, Version = "1.0", CamelCase = camel };
    }

    private string Json(string name) => _contract.CamelCase && name.Length > 0 ? char.ToLowerInvariant(name[0]) + name[1..] : name;

    public ApiContract Build()
    {
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in _model.Endpoints)
        {
            var info = rec.Info;
            if (info.Route == "/" || info.Route.Contains("swagger") || info.Route.Contains("scalar") || rec.Chain.Any(c => c.Name == "ExcludeFromDescription")
                || rec.Method?.AttributeLists.SelectMany(a => a.Attributes).Any(a => ProjectAnalyzer.AttributeName(a) is "ApiExplorerSettings" or "NonAction") == true) continue;
            var raw = RawParams(rec);
            var path = Regex.Replace(info.Route, @"\{\*?([A-Za-z0-9_]+)(:[^}?]*)?\??\}", "{$1}");
            if (!path.StartsWith('/')) path = "/" + path;
            var pathParams = new List<ParamDef>();
            var query = new List<ParamDef>();
            var headers = new List<ParamDef>();
            TypeRef? body = null;
            foreach (var p in info.Parameters)
            {
                var type = Resolve(raw.GetValueOrDefault(p.Name) ?? p.Type, []);
                switch (p.Source)
                {
                    case ParamSource.Route: pathParams.Add(new ParamDef(p.Name, p.Name, type, false)); break;
                    case ParamSource.Header: headers.Add(new ParamDef(p.Name, p.Name, type, p.Optional)); break;
                    case ParamSource.Body: body = type; break;
                    case ParamSource.Query:
                        // A class bound from the query string becomes one parameter per property
                        if (type.Kind == "ref" && _contract.Types.TryGetValue(type.Name!, out var def) && def.Enum is null)
                            query.AddRange(def.Props.Select(x => new ParamDef(x.JsonName, x.JsonName, x.Type, true)));
                        else query.Add(new ParamDef(p.Name, p.Name, type, p.Optional || type.Nullable));
                        break;
                }
            }
            // Route values that aren't method parameters still need to be filled in
            foreach (Match m in Regex.Matches(path, @"\{([A-Za-z0-9_]+)\}"))
                if (pathParams.All(x => !x.Name.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)))
                    pathParams.Add(new ParamDef(m.Groups[1].Value, m.Groups[1].Value, new TypeRef("string"), false));

            var name = OperationName(info);
            var tag = Tag(rec);
            var key = tag + "." + name;
            if (used.TryGetValue(key, out var n)) { used[key] = n + 1; name += n + 1; } else used[key] = 1;
            _contract.Operations.Add(new OperationDef(info.Id, name, tag, info.HttpMethod.ToUpperInvariant(), path, info.Summary, info.RequiresAuth,
                pathParams, query, headers, body, ResponseType(rec)));
        }
        return _contract;
    }

    private static string OperationName(EndpointInfo info)
    {
        var n = Regex.Replace(info.Name, @"Async$", "");
        if (string.IsNullOrWhiteSpace(n) || n.Contains(' ')) n = info.HttpMethod.ToLowerInvariant() + string.Concat(Regex.Split(info.Route, @"[^A-Za-z0-9]+").Where(s => s.Length > 0 && s != "api").Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
        n = Regex.Replace(n, @"[^A-Za-z0-9_]", "");
        if (n.Length == 0 || char.IsDigit(n[0])) n = "call" + n;
        return char.ToLowerInvariant(n[0]) + n[1..];
    }

    private static string Tag(EndpointRecord rec)
    {
        var t = Regex.Replace(rec.Group.Name ?? "", @"(Controller|Endpoints|Api)$", "");
        t = Regex.Replace(t, @"[^A-Za-z0-9]", "");
        if (t.Length == 0) t = "default";
        return char.ToLowerInvariant(t[0]) + t[1..];
    }

    // ------------------------------------------------------------------ raw C# types from the syntax

    private static Dictionary<string, string> RawParams(EndpointRecord rec)
    {
        var list = rec.Method?.ParameterList.Parameters
                   ?? (rec.MapCall?.ArgumentList.Arguments.LastOrDefault()?.Expression as ParenthesizedLambdaExpressionSyntax)?.ParameterList.Parameters;
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (list is null) return dict;
        foreach (var p in list.Value) if (p.Type is not null) dict[p.Identifier.Text] = p.Type.ToString();
        return dict;
    }

    private TypeRef? ResponseType(EndpointRecord rec)
    {
        string? raw = null;
        // [ProducesResponseType(typeof(X), 200)] / [ProducesResponseType<X>(200)] / [ResponseType(typeof(X))]
        var attrs = rec.Method?.AttributeLists.SelectMany(a => a.Attributes) ?? [];
        foreach (var a in attrs)
        {
            var an = ProjectAnalyzer.AttributeName(a);
            if (an is not ("ProducesResponseType" or "ResponseType" or "Produces")) continue;
            if (a.Name is GenericNameSyntax g) { raw = g.TypeArgumentList.Arguments[0].ToString(); break; }
            var typeOf = a.ArgumentList?.Arguments.Select(x => x.Expression).OfType<TypeOfExpressionSyntax>().FirstOrDefault();
            var status = a.ArgumentList?.Arguments.Select(x => x.Expression.ToString()).FirstOrDefault(x => Regex.IsMatch(x, @"^(200|201|StatusCodes\.Status20[01]\w*|HttpStatusCode\.(OK|Created))$"));
            if (typeOf is not null && (status is not null || a.ArgumentList!.Arguments.Count == 1)) { raw = typeOf.Type.ToString(); break; }
        }
        // Minimal API: .Produces<T>() in the chain
        if (raw is null && rec.Chain.FirstOrDefault(c => c.Name == "Produces").Call is { Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax pg } })
            raw = pg.TypeArgumentList.Arguments[0].ToString();
        // Controller method return type
        if (raw is null && rec.Method is not null) raw = Unwrap(rec.Method.ReturnType.ToString());
        // TypedResults.Ok<T> / Results<Ok<T>, NotFound> in a lambda's declared return type
        if (raw is null && rec.MapCall?.ArgumentList.Arguments.LastOrDefault()?.Expression is ParenthesizedLambdaExpressionSyntax { ReturnType: { } lrt })
            raw = Unwrap(lrt.ToString());
        // Otherwise: what the service operation it calls returns
        if (raw is null && rec.Info.Calls is { } calls)
        {
            var parts = calls.Split('.');
            var op = _model.Services.FirstOrDefault(s => s.Interface == parts[0])?.Operations.FirstOrDefault(o => o.Name == parts[^1]);
            if (op is not null) raw = Unwrap(op.ReturnType);
            // A yes/no result from the service usually becomes 204 No Content / 404, not a JSON body
            if (raw is "bool" or "Boolean" && rec.Info.HttpMethod.ToUpperInvariant() is "PUT" or "PATCH" or "DELETE") raw = "void";
        }
        if (raw is null) return rec.Info.HttpMethod is "DELETE" ? null : TypeRef.Any;
        return raw == "void" ? null : Resolve(raw, []);
    }

    /// <summary>Removes wrappers that don't appear in the JSON: Task, ActionResult, Ok&lt;T&gt;… Returns null when the type says nothing.</summary>
    private static string? Unwrap(string t)
    {
        t = t.Trim();
        for (var i = 0; i < 6; i++)
        {
            var (name, args) = SplitGeneric(t);
            var shortName = name.Split('.').Last();
            if (shortName is "Task" or "ValueTask" or "ActionResult" or "Ok" or "Created" or "CreatedAtRoute" or "Accepted" or "JsonHttpResult" && args.Count == 1) { t = args[0]; continue; }
            if (shortName is "Results" && args.Count > 0) { var ok = args.FirstOrDefault(a => Regex.IsMatch(a, @"^(Ok|Created|CreatedAtRoute|Accepted)<")); if (ok is null) return null; t = ok; continue; }
            break;
        }
        var simple = SplitGeneric(t).Name.Split('.').Last();
        if (simple is "Task" or "ValueTask") return "void";
        if (simple is "IActionResult" or "ActionResult" or "IHttpActionResult" or "HttpResponseMessage" or "IResult" or "object" or "NoContent" or "NotFound" or "Ok") return null;
        return t;
    }

    // ------------------------------------------------------------------ C# type → wire type

    private TypeRef Resolve(string type, List<string> typeParams)
    {
        type = type.Trim();
        var nullable = type.EndsWith('?');
        if (nullable) type = type[..^1].Trim();
        var r = ResolveCore(type, typeParams);
        return nullable ? r.AsNullable() : r;
    }

    private TypeRef ResolveCore(string type, List<string> typeParams)
    {
        if (typeParams.Contains(type)) return new TypeRef("param", type);
        if (type.EndsWith("[]")) return type == "byte[]" ? new TypeRef("string", Format: "byte") : new TypeRef("array", Element: Resolve(type[..^2], typeParams));
        var (name, args) = SplitGeneric(type);
        var s = name.Split('.').Last();
        switch (s)
        {
            case "string" or "String" or "char" or "Char": return new TypeRef("string");
            case "int" or "Int32" or "short" or "Int16" or "byte" or "sbyte" or "ushort" or "uint": return new TypeRef("integer", Format: "int32");
            case "long" or "Int64" or "ulong": return new TypeRef("integer", Format: "int64");
            case "decimal" or "Decimal" or "double" or "Double": return new TypeRef("number", Format: "double");
            case "float" or "Single": return new TypeRef("number", Format: "float");
            case "bool" or "Boolean": return new TypeRef("boolean");
            case "DateTime" or "DateTimeOffset": return new TypeRef("string", Format: "date-time");
            case "DateOnly": return new TypeRef("string", Format: "date");
            case "TimeOnly" or "TimeSpan": return new TypeRef("string", Format: "time");
            case "Guid": return new TypeRef("string", Format: "uuid");
            case "Uri": return new TypeRef("string", Format: "uri");
            case "IFormFile": return new TypeRef("string", Format: "binary");
            case "object" or "dynamic" or "JsonElement" or "JsonNode" or "JToken" or "JObject" or "JsonObject" or "JsonDocument": return TypeRef.Any;
            case "Nullable" when args.Count == 1: return Resolve(args[0], typeParams).AsNullable();
            case "List" or "IList" or "IEnumerable" or "ICollection" or "IReadOnlyList" or "IReadOnlyCollection" or "HashSet" or "ISet" or "Collection" or "IQueryable" or "IAsyncEnumerable" when args.Count == 1:
                return new TypeRef("array", Element: Resolve(args[0], typeParams));
            case "Dictionary" or "IDictionary" or "IReadOnlyDictionary" when args.Count == 2:
                return new TypeRef("map", Element: Resolve(args[1], typeParams));
        }
        if (_enums.TryGetValue(s, out var en)) { DefineEnum(en); return new TypeRef("ref", s); }
        if (_types.TryGetValue(s, out var td))
        {
            Define(td);
            return new TypeRef("ref", s, args.Count > 0 ? args.Select(a => Resolve(a, typeParams)).ToList() : null);
        }
        if (FindSymbol(s, args.Count) is { } sym)
        {
            DefineSymbol(sym);
            return new TypeRef("ref", s, args.Count > 0 ? args.Select(a => Resolve(a, typeParams)).ToList() : null);
        }
        return TypeRef.Any;
    }

    private void DefineEnum(EnumDeclarationSyntax e)
    {
        var name = e.Identifier.Text;
        if (_contract.Types.ContainsKey(name)) return;
        var values = new List<(string, long)>();
        long next = 0;
        foreach (var m in e.Members)
        {
            if (m.EqualsValue?.Value is { } v && long.TryParse(v.ToString(), out var parsed)) next = parsed;
            values.Add((m.Identifier.Text, next));
            next++;
        }
        var stringEnum = _stringEnums || e.AttributeLists.SelectMany(a => a.Attributes).Any(a => a.ToString().Contains("StringEnumConverter"));
        _contract.Types[name] = new TypeDef(name, [], [], values, stringEnum, Doc(e));
    }

    private void Define(TypeDeclarationSyntax td)
    {
        var name = td.Identifier.Text;
        if (_contract.Types.ContainsKey(name) || !_building.Add(name)) return;
        var typeParams = td.TypeParameterList?.Parameters.Select(p => p.Identifier.Text).ToList() ?? [];
        // Placeholder first, so self-references terminate
        _contract.Types[name] = new TypeDef(name, typeParams, [], null, false, Doc(td));
        var props = new List<PropDef>();
        // Base class members first
        if (td.BaseList?.Types.FirstOrDefault()?.Type is { } baseType && _types.TryGetValue(ProjectAnalyzer.SimpleName(baseType), out var baseTd) && baseTd != td)
        {
            Define(baseTd);
            var (_, baseArgs) = SplitGeneric(baseType.ToString());
            var baseDef = _contract.Types[baseTd.Identifier.Text];
            var map = baseDef.TypeParams.Zip(baseArgs.Select(a => Resolve(a, typeParams))).ToDictionary(x => x.First, x => x.Second);
            props.AddRange(baseDef.Props.Select(p => p with { Type = Substitute(p.Type, map) }));
        }
        // Positional record parameters
        if (td is RecordDeclarationSyntax { ParameterList: { } pl })
            foreach (var p in pl.Parameters)
                if (p.Type is not null) props.Add(Prop(p.Identifier.Text, p.Type.ToString(), p.AttributeLists, typeParams, p.Default is not null));
        foreach (var p in td.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (!p.Modifiers.Any(SyntaxKind.PublicKeyword) || p.Modifiers.Any(SyntaxKind.StaticKeyword)) continue;
            if (p.AttributeLists.SelectMany(a => a.Attributes).Any(a => ProjectAnalyzer.AttributeName(a) is "JsonIgnore" or "IgnoreDataMember")) continue;
            if (p.AccessorList is not null && !p.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ) continue;
            var prop = Prop(p.Identifier.Text, p.Type.ToString(), p.AttributeLists, typeParams, false);
            props.RemoveAll(x => x.JsonName == prop.JsonName);
            props.Add(prop with { Description = Doc(p) });
        }
        _contract.Types[name] = new TypeDef(name, typeParams, props, null, false, Doc(td));
        _building.Remove(name);
    }

    private PropDef Prop(string name, string type, SyntaxList<AttributeListSyntax> attrLists, List<string> typeParams, bool hasDefault)
    {
        var attrs = attrLists.SelectMany(a => a.Attributes).ToList();
        var jsonName = attrs.Where(a => ProjectAnalyzer.AttributeName(a) is "JsonProperty" or "JsonPropertyName" or "DataMember")
            .Select(ProjectAnalyzer.FirstStringArg).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? Json(name);
        var t = Resolve(type, typeParams);
        var valueLike = t.Kind is "integer" or "number" or "boolean" || t.Format is "date-time" or "date" or "time" or "uuid"
                        || t.Kind == "ref" && _enums.ContainsKey(t.Name!);
        var required = attrs.Any(a => ProjectAnalyzer.AttributeName(a) == "Required")
                       || !t.Nullable && !hasDefault && t.Kind != "any" && (valueLike || _model.NullableEnabled);
        return new PropDef(jsonName, t, required, null);
    }

    private static TypeRef Substitute(TypeRef t, Dictionary<string, TypeRef> map) => t.Kind switch
    {
        "param" when t.Name is not null && map.TryGetValue(t.Name, out var v) => t.Nullable ? v.AsNullable() : v,
        "array" or "map" => t with { Element = Substitute(t.Element!, map) },
        "ref" when t.Args is not null => t with { Args = t.Args.Select(a => Substitute(a, map)).ToList() },
        _ => t,
    };

    private static string? Doc(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia().Select(t => t.GetStructure()).OfType<DocumentationCommentTriviaSyntax>().FirstOrDefault();
        var summary = trivia?.Content.OfType<XmlElementSyntax>().FirstOrDefault(e => e.StartTag.Name.ToString() == "summary");
        if (summary is null) return null;
        var text = Regex.Replace(string.Concat(summary.Content.Select(c => c.ToString())), @"<[^>]+>|///", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 0 ? text : null;
    }

    // ------------------------------------------------------------------ classes inside referenced DLLs

    private Dictionary<string, INamedTypeSymbol>? _dll;

    private INamedTypeSymbol? FindSymbol(string name, int arity)
    {
        if (_dll is null)
        {
            _dll = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
            var dlls = new List<string>();
            foreach (var csproj in new[] { _model.CsprojPath }.Concat(_model.ReferencedProjects.Select(r => r.Path)))
            {
                try
                {
                    foreach (var hint in System.Xml.Linq.XDocument.Load(csproj).Descendants().Where(e => e.Name.LocalName == "HintPath"))
                    {
                        var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csproj)!, hint.Value.Trim()));
                        if (File.Exists(full)) dlls.Add(full);
                    }
                }
                catch { /* ignore */ }
            }
            if (dlls.Count > 0)
            {
                try
                {
                    var refs = dlls.Distinct(StringComparer.OrdinalIgnoreCase).Select(d => (MetadataReference)MetadataReference.CreateFromFile(d)).ToList();
                    var comp = CSharpCompilation.Create("contract", references: refs.Append(MetadataReference.CreateFromFile(typeof(object).Assembly.Location)));
                    foreach (var r in refs)
                        if (comp.GetAssemblyOrModuleSymbol(r) is IAssemblySymbol asm) Collect(asm.GlobalNamespace);
                }
                catch { /* unreadable DLLs */ }
            }
        }
        return _dll.TryGetValue($"{name}`{arity}", out var t) ? t : null;

        void Collect(INamespaceSymbol ns)
        {
            foreach (var t in ns.GetTypeMembers().Where(t => t.DeclaredAccessibility == Accessibility.Public)) _dll!.TryAdd($"{t.Name}`{t.Arity}", t);
            foreach (var c in ns.GetNamespaceMembers()) Collect(c);
        }
    }

    private void DefineSymbol(INamedTypeSymbol sym)
    {
        var name = sym.Name;
        if (_contract.Types.ContainsKey(name) || !_building.Add(name)) return;
        if (sym.TypeKind == TypeKind.Enum)
        {
            var vals = sym.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).Select(f => (f.Name, Convert.ToInt64(f.ConstantValue))).ToList();
            _contract.Types[name] = new TypeDef(name, [], [], vals, _stringEnums, null);
            _building.Remove(name);
            return;
        }
        var typeParams = sym.TypeParameters.Select(p => p.Name).ToList();
        _contract.Types[name] = new TypeDef(name, typeParams, [], null, false, null);
        var props = new List<PropDef>();
        var chain = new List<INamedTypeSymbol>();
        for (var t = sym; t is not null && t.SpecialType != SpecialType.System_Object && chain.Count < 5; t = t.BaseType) chain.Insert(0, t);
        foreach (var t in chain)
            foreach (var p in t.GetMembers().OfType<IPropertySymbol>())
            {
                if (p.DeclaredAccessibility != Accessibility.Public || p.IsStatic || p.IsIndexer) continue;
                if (p.GetAttributes().Any(a => a.AttributeClass?.Name is "JsonIgnoreAttribute" or "IgnoreDataMemberAttribute")) continue;
                var jsonName = p.GetAttributes().Where(a => a.AttributeClass?.Name is "JsonPropertyAttribute" or "JsonPropertyNameAttribute")
                    .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? Json(p.Name);
                var typeText = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                var tr = Resolve(typeText, typeParams);
                props.RemoveAll(x => x.JsonName == jsonName);
                props.Add(new PropDef(jsonName, tr, !tr.Nullable && p.Type.IsValueType, null));
            }
        _contract.Types[name] = new TypeDef(name, typeParams, props, null, false, null);
        _building.Remove(name);
    }

    internal static (string Name, List<string> Args) SplitGeneric(string type)
    {
        var i = type.IndexOf('<');
        if (i < 0 || !type.EndsWith('>')) return (type, []);
        var inner = type[(i + 1)..^1];
        var args = new List<string>();
        int depth = 0, start = 0;
        for (var k = 0; k < inner.Length; k++)
        {
            if (inner[k] == '<') depth++;
            else if (inner[k] == '>') depth--;
            else if (inner[k] == ',' && depth == 0) { args.Add(inner[start..k].Trim()); start = k + 1; }
        }
        args.Add(inner[start..].Trim());
        return (type[..i], args);
    }
}
