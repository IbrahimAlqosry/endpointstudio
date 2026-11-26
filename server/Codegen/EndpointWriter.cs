using System.Text.RegularExpressions;
using EndpointStudio.Server.Models;
using EndpointStudio.Server.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointStudio.Server.Codegen;

/// <summary>Adds, edits and removes endpoints in a user's project.</summary>
public class EndpointWriter(HistoryService history)
{
    private static readonly Regex IdentifierRx = new("^[A-Za-z_][A-Za-z0-9_]*$");
    private static readonly Regex RouteRx = new(@"^[A-Za-z0-9_\-./{}:?*~=]*$");

    public static readonly Dictionary<string, string> ParamTypes = new()
    {
        ["string"] = "string", ["int"] = "int", ["long"] = "long", ["decimal"] = "decimal", ["double"] = "double",
        ["bool"] = "bool", ["Guid"] = "Guid", ["DateTime"] = "DateTime", ["DateOnly"] = "DateOnly", ["json"] = "JsonElement",
    };

    // ================================================================== ADD

    public string Add(string csproj, AddEndpointRequest req)
    {
        var model = ProjectAnalyzer.Analyze(csproj);
        var verb = ValidateVerb(req.HttpMethod);
        var name = ValidateName(req.Name);
        var route = ValidateRoute(req.Route);

        GroupRecord? group = null;
        if (req.NewGroup is null)
        {
            group = model.Groups.FirstOrDefault(g => g.Key == req.GroupKey)
                    ?? throw new StudioException("The selected group no longer exists. Please refresh the project.");
        }

        var style = group?.Style ?? req.NewGroup!.Style;
        var baseRoute = group?.BaseRoute ?? ProjectAnalyzer.NormalizeRoute(req.NewGroup!.BaseRoute);
        var fullRoute = ProjectAnalyzer.CombineRoutes(baseRoute, route);
        EnsureNoConflict(model, verb, fullRoute, null);
        EnsureNameAvailable(model, style, group, name, null);

        var plan = BuildPlan(model, req, verb, route, fullRoute);
        var changes = history.Begin(model.CsprojPath, $"Added {verb} {fullRoute}");

        if (group is not null && style == EndpointStyle.Controller) AddToController(model, group, changes, plan, verb, route, name, req);
        else if (group is not null) AddToMinimalGroup(model, group, changes, plan, verb, route, name, req);
        else if (style == EndpointStyle.Controller) CreateController(model, req.NewGroup!, changes, plan, verb, route, name, req);
        else CreateMinimalGroup(model, req.NewGroup!, changes, plan, verb, route, name, req);

        changes.Commit();
        return $"{verb} {fullRoute} was added.";
    }

    // ---------- Generation plan (what parameters and logic the endpoint gets) ----------

    private sealed record GenParam(string Name, string Type, ParamSource Source, bool Optional);

    private sealed class Plan
    {
        public List<GenParam> Params { get; } = [];
        public List<string> Body { get; } = []; // uses tokens: Ok(x) etc rendered per style
        public bool Async { get; set; }
        public HashSet<string> Usings { get; } = ["Microsoft.AspNetCore.Mvc"];
        public EntityInfo? Entity { get; set; }
    }

    private static Plan BuildPlan(ProjectModel model, AddEndpointRequest req, string verb, string route, string fullRoute)
    {
        var plan = new Plan();
        var routeParams = ProjectAnalyzer.RouteParameterNames(route);

        if (Behaviors.IsEntity(req.Behavior))
        {
            var e = model.Entities.FirstOrDefault(x => x.Name == req.Entity)
                    ?? throw new StudioException("Please choose which data table this endpoint works with.");
            plan.Entity = e;
            plan.Async = true;
            plan.Usings.Add("Microsoft.EntityFrameworkCore");
            plan.Usings.Add(e.Namespace);
            plan.Usings.Add(e.ContextNamespace);
            var set = $"db.{e.DbSetName}";
            var needsKey = req.Behavior is Behaviors.EntityGet or Behaviors.EntityUpdate or Behaviors.EntityDelete;
            if (needsKey)
            {
                if (e.KeyName is null || e.KeyType is null)
                    throw new StudioException($"{e.Name} doesn't have a single ID column, so it can't be looked up by ID.");
                var keyParam = routeParams.FirstOrDefault() ?? throw new StudioException("This endpoint needs the ID in its path, for example {id}.");
                if (routeParams.Count > 1) throw new StudioException("Only one {value} placeholder is allowed in the path for this kind of endpoint.");
                plan.Params.Add(new GenParam(keyParam, e.KeyType, ParamSource.Route, false));

                var findLines = new[]
                {
                    $"var existing = await {set}.FindAsync({keyParam});",
                };
                switch (req.Behavior)
                {
                    case Behaviors.EntityGet:
                        plan.Body.Add(findLines[0]);
                        plan.Body.Add("return existing is null ? @NotFound() : @Ok(existing);");
                        break;
                    case Behaviors.EntityUpdate:
                        plan.Params.Add(new GenParam("item", e.Name, ParamSource.Body, false));
                        plan.Body.Add(findLines[0]);
                        plan.Body.Add("if (existing is null) return @NotFound();");
                        plan.Body.Add($"item.{e.KeyName} = {keyParam};");
                        plan.Body.Add("db.Entry(existing).CurrentValues.SetValues(item);");
                        plan.Body.Add("await db.SaveChangesAsync();");
                        plan.Body.Add("return @Ok(existing);");
                        break;
                    case Behaviors.EntityDelete:
                        plan.Body.Add(findLines[0]);
                        plan.Body.Add("if (existing is null) return @NotFound();");
                        plan.Body.Add($"{set}.Remove(existing);");
                        plan.Body.Add("await db.SaveChangesAsync();");
                        plan.Body.Add("return @NoContent();");
                        break;
                }
            }
            else
            {
                if (routeParams.Count > 0) throw new StudioException("This kind of endpoint doesn't use {placeholders} in its path.");
                switch (req.Behavior)
                {
                    case Behaviors.EntityList:
                        plan.Params.Add(new GenParam("page", "int", ParamSource.Query, true));
                        plan.Params.Add(new GenParam("pageSize", "int", ParamSource.Query, true));
                        plan.Body.Add("var pageNumber = Math.Max(page ?? 1, 1);");
                        plan.Body.Add("var size = Math.Clamp(pageSize ?? 50, 1, 500);");
                        plan.Body.Add($"var items = await {set}.AsNoTracking()");
                        if (e.KeyName is not null) plan.Body.Add($"    .OrderBy(x => x.{e.KeyName})");
                        plan.Body.Add("    .Skip((pageNumber - 1) * size)");
                        plan.Body.Add("    .Take(size)");
                        plan.Body.Add("    .ToListAsync();");
                        plan.Body.Add("return @Ok(items);");
                        break;
                    case Behaviors.EntityCreate:
                        plan.Params.Add(new GenParam("item", e.Name, ParamSource.Body, false));
                        plan.Body.Add($"{set}.Add(item);");
                        plan.Body.Add("await db.SaveChangesAsync();");
                        plan.Body.Add("return @Created(item);");
                        break;
                    case Behaviors.EntityFilter:
                    {
                        var prop = e.Properties.FirstOrDefault(p => p.Name == req.FilterProperty)
                                   ?? throw new StudioException("Please choose which field to search by.");
                        var pname = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
                        if (SyntaxFacts.GetKeywordKind(pname) != SyntaxKind.None || pname == "db") pname = "value";
                        plan.Params.Add(new GenParam(pname, prop.Type, ParamSource.Query, false));
                        var cond = prop.Type == "string"
                            ? $"x.{prop.Name} != null && x.{prop.Name}.Contains({pname})"
                            : $"x.{prop.Name} == {pname}";
                        plan.Body.Add($"var items = await {set}.AsNoTracking()");
                        plan.Body.Add($"    .Where(x => {cond})");
                        plan.Body.Add("    .ToListAsync();");
                        plan.Body.Add("return @Ok(items);");
                        break;
                    }
                    default:
                        throw new StudioException("Unknown endpoint behavior.");
                }
            }
            plan.Params.Add(new GenParam("db", e.ContextName, ParamSource.Service, false));
            return plan;
        }

        // ---- Simple behaviors with user-defined inputs ----
        var userParams = req.Parameters ?? [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in userParams)
        {
            if (!IdentifierRx.IsMatch(p.Name) || SyntaxFacts.GetKeywordKind(p.Name) != SyntaxKind.None)
                throw new StudioException($"\"{p.Name}\" isn't a valid input name. Use letters and numbers only, starting with a letter.");
            if (!names.Add(p.Name)) throw new StudioException($"The input \"{p.Name}\" is listed twice.");
            var type = ResolveParamType(model, p.Type, plan);
            if (p.Source == ParamSource.Route && !routeParams.Contains(p.Name))
                throw new StudioException($"The path input \"{p.Name}\" must appear in the path as {{{p.Name}}}.");
            if (p.Source == ParamSource.Service) throw new StudioException("Service inputs can't be added from here.");
            plan.Params.Add(new GenParam(p.Name, type, p.Source, p.Source != ParamSource.Route && p.Optional));
        }
        foreach (var rp in routeParams.Where(rp => !names.Contains(rp)))
        {
            if (!IdentifierRx.IsMatch(rp)) throw new StudioException($"The path placeholder {{{rp}}} isn't valid.");
            plan.Params.Insert(0, new GenParam(rp, "string", ParamSource.Route, false));
        }
        if (plan.Params.Count(p => p.Source == ParamSource.Body) > 1)
            throw new StudioException("An endpoint can only receive one body. Combine the fields into a single JSON object.");
        if (plan.Params.Any(p => p.Source == ParamSource.Body) && verb is "GET" or "DELETE")
            throw new StudioException($"{verb} endpoints shouldn't receive a body. Use path or query inputs instead.");

        switch (req.Behavior)
        {
            case Behaviors.Message:
                var msg = string.IsNullOrWhiteSpace(req.Message) ? "Hello from " + fullRoute : req.Message.Trim();
                plan.Body.Add($"return @Ok(new {{ message = {TextEdits.Literal(msg)} }});");
                break;
            case Behaviors.Echo:
                var fields = plan.Params.Select(p => p.Name).ToList();
                plan.Body.Add(fields.Count == 0
                    ? "return @Ok(new { received = true, at = DateTime.UtcNow });"
                    : $"return @Ok(new {{ {string.Join(", ", fields)} }});");
                break;
            case Behaviors.NotImplemented:
                plan.Body.Add("return @NotImplemented();");
                break;
            case Behaviors.NoContent:
                plan.Body.Add("return @NoContent();");
                break;
            default:
                throw new StudioException("Unknown endpoint behavior.");
        }
        return plan;
    }

    private static string ResolveParamType(ProjectModel model, string type, Plan plan)
    {
        if (ParamTypes.TryGetValue(type, out var t))
        {
            if (t == "JsonElement") plan.Usings.Add("System.Text.Json");
            return t;
        }
        var entity = model.Entities.FirstOrDefault(e => e.Name == type);
        if (entity is not null)
        {
            plan.Usings.Add(entity.Namespace);
            return entity.Name;
        }
        throw new StudioException($"The input type \"{type}\" isn't supported.");
    }

    // ---------- Rendering ----------

    private static string RenderResult(string line, EndpointStyle style)
    {
        string R(string controller, string minimal) => style == EndpointStyle.Controller ? controller : minimal;
        line = Regex.Replace(line, @"@Ok\(", R("Ok(", "Results.Ok("));
        line = line.Replace("@NotFound()", R("NotFound()", "Results.NotFound()"));
        line = line.Replace("@NoContent()", R("NoContent()", "Results.NoContent()"));
        line = line.Replace("@NotImplemented()", R(
            "StatusCode(501, new { message = \"This endpoint is not implemented yet.\" })",
            "Results.Json(new { message = \"This endpoint is not implemented yet.\" }, statusCode: 501)"));
        line = Regex.Replace(line, @"@Created\((\w+)\)", m => R($"StatusCode(201, {m.Groups[1].Value})", $"Results.Json({m.Groups[1].Value}, statusCode: 201)"));
        return line;
    }

    private static string RenderParam(GenParam p, EndpointStyle style)
    {
        var attr = p.Source switch
        {
            ParamSource.Route => "[FromRoute] ",
            ParamSource.Query => "[FromQuery] ",
            ParamSource.Body => "[FromBody] ",
            ParamSource.Header => "[FromHeader] ",
            ParamSource.Service => style == EndpointStyle.Controller ? "[FromServices] " : "",
            _ => "",
        };
        var type = p.Type + (p.Optional ? "?" : "");
        var def = p.Optional && style == EndpointStyle.Controller ? " = null" : "";
        return $"{attr}{type} {p.Name}{def}";
    }

    private static string RenderControllerMethod(Plan plan, string verb, string route, string name, string? summary, bool auth, string ind, string nl)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(summary)) lines.AddRange(DocLines(summary, ind));
        lines.Add($"{ind}[{HttpAttribute(verb, route)}]");
        if (auth) lines.Add($"{ind}[Authorize]");
        var ret = plan.Async ? "async Task<IActionResult>" : "IActionResult";
        var ordered = plan.Params.Where(p => !p.Optional).Concat(plan.Params.Where(p => p.Optional)); // C# needs optional parameters last
        lines.Add($"{ind}public {ret} {name}({string.Join(", ", ordered.Select(p => RenderParam(p, EndpointStyle.Controller)))})");
        lines.Add($"{ind}{{");
        lines.AddRange(plan.Body.Select(b => $"{ind}    {RenderResult(b, EndpointStyle.Controller)}"));
        lines.Add($"{ind}}}");
        return string.Join(nl, lines);
    }

    private static string RenderMinimal(Plan plan, ProjectModel model, string receiver, string verb, string route, string name, string? summary, bool auth, string ind, string nl)
    {
        var lines = new List<string>();
        var asyncKw = plan.Async ? "async " : "";
        var mapName = "Map" + char.ToUpperInvariant(verb[0]) + verb[1..].ToLowerInvariant();
        lines.Add($"{ind}{receiver}.{mapName}({TextEdits.Literal(route)}, {asyncKw}({string.Join(", ", plan.Params.Select(p => RenderParam(p, EndpointStyle.MinimalApi)))}) =>");
        lines.Add($"{ind}{{");
        lines.AddRange(plan.Body.Select(b => $"{ind}    {RenderResult(b, EndpointStyle.MinimalApi)}"));
        lines.Add($"{ind}}})");
        lines.Add($"{ind}.WithName({TextEdits.Literal(name)})");
        if (!string.IsNullOrWhiteSpace(summary) && model.FrameworkVersion is 0 or >= 7) lines.Add($"{ind}.WithSummary({TextEdits.Literal(summary.Trim())})");
        if (auth) lines.Add($"{ind}.RequireAuthorization()");
        lines[^1] += ";";
        return string.Join(nl, lines);
    }

    private static string HttpAttribute(string verb, string route)
    {
        var a = "Http" + char.ToUpperInvariant(verb[0]) + verb[1..].ToLowerInvariant();
        return string.IsNullOrEmpty(route) ? a : $"{a}({TextEdits.Literal(route)})";
    }

    private static IEnumerable<string> DocLines(string summary, string ind)
    {
        yield return $"{ind}/// <summary>";
        foreach (var l in summary.Trim().Split('\n'))
            yield return $"{ind}/// {System.Security.SecurityElement.Escape(l.Trim())}";
        yield return $"{ind}/// </summary>";
    }

    // ---------- Placement ----------

    private static void AddToController(ProjectModel model, GroupRecord group, HistoryService.ChangeSet changes, Plan plan, string verb, string route, string name, AddEndpointRequest req)
    {
        var cls = group.Class!;
        var edits = new TextEdits(group.File.Text);
        var nl = edits.NewLine;
        var memberIndent = cls.Members.Count > 0 ? edits.IndentAt(cls.Members[0].SpanStart) : edits.IndentAt(cls.SpanStart) + "    ";
        if (req.RequiresAuth && !group.ClassRequiresAuth) plan.Usings.Add("Microsoft.AspNetCore.Authorization");
        var method = RenderControllerMethod(plan, verb, route, name, req.Summary, req.RequiresAuth && !group.ClassRequiresAuth, memberIndent, nl);

        var close = cls.CloseBraceToken.SpanStart;
        if (edits.OnlyWhitespaceBefore(close))
            edits.Insert(edits.LineStart(close), (cls.Members.Count > 0 ? nl : "") + method + nl);
        else
            edits.Insert(close, nl + method + nl + edits.IndentAt(cls.SpanStart));

        edits.AddUsings(group.File.Root, plan.Usings, ProjectAnalyzer.NamespaceOf(cls));
        Save(changes, group.File, edits);
    }

    private static void AddToMinimalGroup(ProjectModel model, GroupRecord group, HistoryService.ChangeSet changes, Plan plan, string verb, string route, string name, AddEndpointRequest req)
    {
        var last = group.Endpoints.LastOrDefault() ?? throw new StudioException("This group has no endpoints to place the new one next to.");
        var stmt = last.OuterCall!.Ancestors().OfType<StatementSyntax>().FirstOrDefault()
                   ?? throw new StudioException("Couldn't find where to place the new endpoint.");
        var anchor = stmt.Parent is GlobalStatementSyntax gs ? (SyntaxNode)gs : stmt;
        var edits = new TextEdits(group.File.Text);
        var nl = edits.NewLine;
        var ind = edits.IndentAt(anchor.SpanStart);
        var code = RenderMinimal(plan, model, group.Receiver!, verb, route, name, req.Summary, req.RequiresAuth && !group.ClassRequiresAuth, ind, nl);
        edits.Insert(anchor.Span.End, nl + nl + code);
        edits.AddUsings(group.File.Root, plan.Usings, ProjectAnalyzer.NamespaceOf(anchor));
        Save(changes, group.File, edits);
    }

    private void CreateController(ProjectModel model, NewGroupRequest ng, HistoryService.ChangeSet changes, Plan plan, string verb, string route, string name, AddEndpointRequest req)
    {
        var groupName = ValidateGroupName(ng.Name);
        var className = groupName.EndsWith("Controller") ? groupName : groupName + "Controller";
        var path = Path.Combine(model.Directory, "Controllers", className + ".cs");
        if (File.Exists(path) || model.Groups.Any(g => g.Class?.Identifier.Text == className))
            throw new StudioException($"A group called \"{groupName}\" already exists.");

        const string nl = "\r\n";
        var ns = model.RootNamespace + ".Controllers";
        if (req.RequiresAuth) plan.Usings.Add("Microsoft.AspNetCore.Authorization");
        var fileScoped = model.FrameworkVersion is 0 or >= 6;
        var ind = fileScoped ? "" : "    ";
        var method = RenderControllerMethod(plan, verb, route, name, req.Summary, req.RequiresAuth, ind + "    ", nl);
        var baseRoute = ProjectAnalyzer.NormalizeRoute(ng.BaseRoute).TrimStart('/');

        var lines = new List<string>();
        lines.AddRange(plan.Usings.Where(u => !string.IsNullOrEmpty(u) && u != ns).Distinct().OrderBy(u => u).Select(u => $"using {u};"));
        lines.Add("");
        if (fileScoped) { lines.Add($"namespace {ns};"); lines.Add(""); }
        else { lines.Add($"namespace {ns}"); lines.Add("{"); }
        lines.Add($"{ind}[ApiController]");
        lines.Add($"{ind}[Route({TextEdits.Literal(baseRoute)})]");
        lines.Add($"{ind}public class {className} : ControllerBase");
        lines.Add($"{ind}{{");
        lines.Add(method);
        lines.Add($"{ind}}}");
        if (!fileScoped) lines.Add("}");
        changes.Write(path, string.Join(nl, lines) + nl);

        EnsureControllersRegistered(model, changes);
    }

    private void CreateMinimalGroup(ProjectModel model, NewGroupRequest ng, HistoryService.ChangeSet changes, Plan plan, string verb, string route, string name, AddEndpointRequest req)
    {
        var groupName = ValidateGroupName(ng.Name);
        var className = groupName + "Endpoints";
        var path = Path.Combine(model.Directory, "Endpoints", className + ".cs");
        if (File.Exists(path)) throw new StudioException($"A group called \"{groupName}\" already exists.");

        const string nl = "\r\n";
        var ns = model.RootNamespace + ".Endpoints";
        var fileScoped = model.FrameworkVersion is 0 or >= 6;
        var ind = fileScoped ? "" : "    ";
        var code = RenderMinimal(plan, model, "group", verb, route, name, req.Summary, req.RequiresAuth, ind + "        ", nl);
        var baseRoute = ProjectAnalyzer.NormalizeRoute(ng.BaseRoute);
        var tagName = ProjectAnalyzer.SplitWords(groupName);

        var usings = new HashSet<string>(plan.Usings) { "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Routing" };
        var lines = new List<string>();
        lines.AddRange(usings.Where(u => !string.IsNullOrEmpty(u) && u != ns).Distinct().OrderBy(u => u).Select(u => $"using {u};"));
        lines.Add("");
        if (fileScoped) { lines.Add($"namespace {ns};"); lines.Add(""); }
        else { lines.Add($"namespace {ns}"); lines.Add("{"); }
        lines.Add($"{ind}public static class {className}");
        lines.Add($"{ind}{{");
        lines.Add($"{ind}    public static IEndpointRouteBuilder Map{className}(this IEndpointRouteBuilder app)");
        lines.Add($"{ind}    {{");
        lines.Add($"{ind}        var group = app.MapGroup({TextEdits.Literal(baseRoute)}).WithTags({TextEdits.Literal(tagName)});");
        lines.Add("");
        lines.Add(code);
        lines.Add("");
        lines.Add($"{ind}        return app;");
        lines.Add($"{ind}    }}");
        lines.Add($"{ind}}}");
        if (!fileScoped) lines.Add("}");
        changes.Write(path, string.Join(nl, lines) + nl);

        // Register in Program.cs
        var program = model.ProgramFile ?? throw new StudioException("Couldn't find Program.cs to register the new group.");
        var edits = new TextEdits(changes.Read(program.Path));
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(edits.Original, ProjectAnalyzer.ParseOptions).GetRoot();
        var runStmt = FindRunStatement(root) ?? throw new StudioException("Couldn't find where the app starts (app.Run) in Program.cs.");
        var appVar = (((runStmt as ExpressionStatementSyntax)?.Expression as InvocationExpressionSyntax)?.Expression as MemberAccessExpressionSyntax)?.Expression.ToString() ?? "app";
        var anchor = runStmt.Parent is GlobalStatementSyntax g ? (SyntaxNode)g : runStmt;
        edits.Insert(edits.LineStart(anchor.SpanStart), $"{edits.IndentAt(anchor.SpanStart)}{appVar}.Map{className}();{edits.NewLine}{edits.NewLine}");
        edits.AddUsings(root, [ns], null);
        var updated = edits.Apply();
        TextEdits.EnsureStillValid(edits.Original, updated);
        changes.Write(program.Path, updated);
    }

    private static void EnsureControllersRegistered(ProjectModel model, HistoryService.ChangeSet changes)
    {
        var all = string.Concat(model.Files.Select(f => f.Text));
        var needAdd = !all.Contains("AddControllers(") && !all.Contains("AddControllersWithViews(") && !all.Contains("AddMvc(");
        var needMap = !all.Contains("MapControllers(") && !all.Contains("MapDefaultControllerRoute(") && !all.Contains("MapControllerRoute(");
        if (!needAdd && !needMap) return;
        var program = model.ProgramFile ?? throw new StudioException("Couldn't find Program.cs to turn on controllers.");
        var edits = new TextEdits(changes.Read(program.Path));
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(edits.Original, ProjectAnalyzer.ParseOptions).GetRoot();
        if (needAdd)
        {
            var build = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Build" } m && m.Expression.ToString().Contains("builder", StringComparison.OrdinalIgnoreCase));
            var stmt = build?.Ancestors().OfType<StatementSyntax>().FirstOrDefault()
                       ?? throw new StudioException("Couldn't find where services are configured in Program.cs.");
            var builderVar = ((MemberAccessExpressionSyntax)build.Expression).Expression.ToString();
            edits.Insert(edits.LineStart(stmt.SpanStart), $"{edits.IndentAt(stmt.SpanStart)}{builderVar}.Services.AddControllers();{edits.NewLine}{edits.NewLine}");
        }
        if (needMap)
        {
            var run = FindRunStatement(root) ?? throw new StudioException("Couldn't find where the app starts (app.Run) in Program.cs.");
            var appVar = (((run as ExpressionStatementSyntax)?.Expression as InvocationExpressionSyntax)?.Expression as MemberAccessExpressionSyntax)?.Expression.ToString() ?? "app";
            edits.Insert(edits.LineStart(run.SpanStart), $"{edits.IndentAt(run.SpanStart)}{appVar}.MapControllers();{edits.NewLine}{edits.NewLine}");
        }
        var updated = edits.Apply();
        TextEdits.EnsureStillValid(edits.Original, updated);
        changes.Write(program.Path, updated);
    }

    private static StatementSyntax? FindRunStatement(CompilationUnitSyntax root) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Run" or "RunAsync" } && i.ArgumentList.Arguments.Count == 0)
            .Select(i => i.Ancestors().OfType<StatementSyntax>().FirstOrDefault())
            .LastOrDefault(s => s is not null);

    private static void Save(HistoryService.ChangeSet changes, SourceFile file, TextEdits edits)
    {
        var updated = edits.Apply();
        TextEdits.EnsureStillValid(edits.Original, updated);
        changes.Write(file.Path, updated);
    }

    // ================================================================== UPDATE

    public string Update(string csproj, string id, UpdateEndpointRequest req)
    {
        var model = ProjectAnalyzer.Analyze(csproj);
        var rec = Find(model, id);
        var verb = ValidateVerb(req.HttpMethod);
        var route = ValidateRoute(req.Route);
        var name = ValidateName(req.Name);
        var fullRoute = ProjectAnalyzer.CombineRoutes(rec.Group.BaseRoute, route);
        EnsureNoConflict(model, verb, fullRoute, rec);
        EnsureNameAvailable(model, rec.Info.Style, rec.Group, name, rec);

        var changes = history.Begin(model.CsprojPath, $"Edited {rec.Info.HttpMethod} {rec.Info.Route}");
        var edits = new TextEdits(rec.File.Text);
        if (rec.Info.Style == EndpointStyle.Controller) UpdateController(rec, edits, verb, route, name, req);
        else UpdateMinimal(model, rec, edits, verb, route, name, req);
        Save(changes, rec.File, edits);
        changes.Commit();
        return $"{verb} {fullRoute} was updated.";
    }

    private static void UpdateController(EndpointRecord rec, TextEdits edits, string verb, string route, string name, UpdateEndpointRequest req)
    {
        var m = rec.Method!;
        var nl = edits.NewLine;
        var ind = edits.IndentAt(m.SpanStart);
        var firstNonAttr = m.Modifiers.Count > 0 ? m.Modifiers[0] : m.ReturnType.GetFirstToken();

        var routeChanged = verb != rec.Info.HttpMethod || route != rec.Info.RelativeRoute;
        var classAuth = rec.Group.ClassRequiresAuth;

        bool Managed(AttributeSyntax a)
        {
            var n = ProjectAnalyzer.AttributeName(a);
            if (routeChanged && (n.StartsWith("Http") || n == "Route")) return true;
            if (req.RequiresAuth != rec.Info.RequiresAuth && (n == "Authorize" || n == "AllowAnonymous")) return true;
            return false;
        }

        var attrLines = new List<string>();
        if (routeChanged) attrLines.Add($"[{HttpAttribute(verb, route)}]");
        foreach (var list in m.AttributeLists)
        {
            var kept = list.Attributes.Where(a => !Managed(a)).ToList();
            if (kept.Count == list.Attributes.Count) attrLines.Add(list.ToString().Trim());
            else if (kept.Count > 0) attrLines.Add("[" + string.Join(", ", kept.Select(k => k.ToString())) + "]");
        }
        if (req.RequiresAuth != rec.Info.RequiresAuth)
        {
            if (req.RequiresAuth && !classAuth) attrLines.Add("[Authorize]");
            if (!req.RequiresAuth && classAuth) attrLines.Add("[AllowAnonymous]");
            if (req.RequiresAuth || classAuth) edits.AddUsings((CompilationUnitSyntax)rec.File.Root, ["Microsoft.AspNetCore.Authorization"], null);
        }

        // Doc comment
        var firstToken = m.GetFirstToken();
        var doc = firstToken.LeadingTrivia.Select(t => t.GetStructure()).OfType<DocumentationCommentTriviaSyntax>().FirstOrDefault();
        var regionStart = edits.LineStart(doc?.SpanStart ?? firstToken.SpanStart);
        var docLines = new List<string>();
        var summary = req.Summary?.Trim();
        if (!string.IsNullOrEmpty(summary)) docLines.AddRange(DocLines(summary, ind));
        if (doc is not null)
        {
            var docText = edits.Original[edits.LineStart(doc.SpanStart)..edits.LineStart(m.AttributeLists.Count > 0 ? m.AttributeLists[0].SpanStart : firstNonAttr.SpanStart)];
            var inSummary = false;
            foreach (var raw in docText.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Trim().Length == 0) continue;
                if (line.Contains("<summary")) inSummary = true;
                if (!inSummary && line.TrimStart().StartsWith("//")) docLines.Add(line);
                if (line.Contains("</summary>")) inSummary = false;
            }
        }

        var region = string.Concat(docLines.Select(l => l + nl)) + string.Concat(attrLines.Select(a => ind + a + nl)) + ind;
        edits.Replace(regionStart, firstNonAttr.SpanStart, region);

        if (name != m.Identifier.Text)
        {
            edits.Replace(m.Identifier.SpanStart, m.Identifier.Span.End, name);
            // Keep nameof(OldName) references in the same class working (outside the regenerated attribute region)
            var regionEnd = firstNonAttr.SpanStart;
            foreach (var n in rec.Group.Class!.DescendantNodes().OfType<InvocationExpressionSyntax>()
                         .Where(i => i.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" })
                         .SelectMany(i => i.ArgumentList.Arguments)
                         .Select(a => a.Expression).OfType<IdentifierNameSyntax>()
                         .Where(x => x.Identifier.Text == m.Identifier.Text && (x.SpanStart >= regionEnd || x.Span.End <= regionStart)))
                edits.Replace(n.SpanStart, n.Span.End, name);
        }
    }

    private static void UpdateMinimal(ProjectModel model, EndpointRecord rec, TextEdits edits, string verb, string route, string name, UpdateEndpointRequest req)
    {
        var ma = (MemberAccessExpressionSyntax)rec.MapCall!.Expression;
        var nl = edits.NewLine;
        var stmt = rec.OuterCall!.Ancestors().OfType<StatementSyntax>().First();
        var chainIndent = edits.IndentAt(stmt.SpanStart) + "    ";
        if (rec.Chain.Count > 0)
        {
            var lastDot = ((MemberAccessExpressionSyntax)rec.Chain[^1].Call.Expression).OperatorToken;
            if (edits.OnlyWhitespaceBefore(lastDot.SpanStart)) chainIndent = edits.IndentAt(lastDot.SpanStart);
        }

        if (verb != rec.Info.HttpMethod)
        {
            if (ma.Name.Identifier.Text == "MapMethods") throw new StudioException("The method of this endpoint can't be changed here.");
            var mapName = "Map" + char.ToUpperInvariant(verb[0]) + verb[1..].ToLowerInvariant();
            edits.Replace(ma.Name.Identifier.SpanStart, ma.Name.Identifier.Span.End, mapName);
        }
        if (route != rec.Info.RelativeRoute)
        {
            var lit = rec.MapCall.ArgumentList.Arguments[0].Expression;
            edits.Replace(lit.SpanStart, lit.Span.End, TextEdits.Literal(route));
        }

        var appends = new List<string>();
        void SetChainArg(string method, string? value)
        {
            var existing = rec.Chain.FirstOrDefault(c => c.Name == method).Call;
            if (existing is not null)
            {
                if (string.IsNullOrWhiteSpace(value)) RemoveChainCall(edits, existing);
                else
                {
                    var arg = existing.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                    if (arg is not null) edits.Replace(arg.SpanStart, arg.Span.End, TextEdits.Literal(value.Trim()));
                }
            }
            else if (!string.IsNullOrWhiteSpace(value))
            {
                appends.Add($".{method}({TextEdits.Literal(value.Trim())})");
            }
        }

        if (name != rec.Info.Name) SetChainArg("WithName", name);
        if ((req.Summary ?? "").Trim() != (rec.Info.Summary ?? "").Trim())
        {
            var hasDescriptionOnly = rec.Chain.All(c => c.Name != "WithSummary") && rec.Chain.Any(c => c.Name == "WithDescription");
            if (hasDescriptionOnly) SetChainArg("WithDescription", req.Summary);
            else if (model.FrameworkVersion is 0 or >= 7) SetChainArg("WithSummary", req.Summary);
            else SetChainArg("WithDescription", req.Summary);
        }

        if (req.RequiresAuth != rec.Info.RequiresAuth)
        {
            if (req.RequiresAuth)
            {
                foreach (var c in rec.Chain.Where(c => c.Name == "AllowAnonymous")) RemoveChainCall(edits, c.Call);
                if (!rec.Group.ClassRequiresAuth) appends.Add(".RequireAuthorization()");
            }
            else
            {
                foreach (var c in rec.Chain.Where(c => c.Name == "RequireAuthorization")) RemoveChainCall(edits, c.Call);
                if (rec.Group.ClassRequiresAuth) appends.Add(".AllowAnonymous()");
            }
        }

        if (appends.Count > 0)
            edits.Insert(rec.OuterCall.Span.End, string.Concat(appends.Select(a => nl + chainIndent + a)));
    }

    private static void RemoveChainCall(TextEdits edits, InvocationExpressionSyntax call)
    {
        var ma = (MemberAccessExpressionSyntax)call.Expression;
        var prev = ma.OperatorToken.GetPreviousToken();
        edits.Remove(prev.Span.End, call.Span.End);
    }

    // ================================================================== DELETE

    public string Delete(string csproj, string id)
    {
        var model = ProjectAnalyzer.Analyze(csproj);
        var rec = Find(model, id);
        var changes = history.Begin(model.CsprojPath, $"Deleted {rec.Info.HttpMethod} {rec.Info.Route}");
        var edits = new TextEdits(rec.File.Text);

        if (rec.Info.Style == EndpointStyle.Controller)
        {
            var m = rec.Method!;
            edits.Remove(m.FullSpan.Start, m.FullSpan.End);
        }
        else
        {
            var stmt = rec.OuterCall!.Parent as ExpressionStatementSyntax
                       ?? throw new StudioException("This endpoint is written in a way that can't be removed automatically.");
            SyntaxNode node = stmt.Parent is GlobalStatementSyntax g ? g : stmt;
            edits.Remove(node.FullSpan.Start, node.FullSpan.End);
        }

        Save(changes, rec.File, edits);
        changes.Commit();
        return $"{rec.Info.HttpMethod} {rec.Info.Route} was deleted.";
    }

    // ================================================================== Validation

    private static EndpointRecord Find(ProjectModel model, string id) =>
        model.Endpoints.FirstOrDefault(e => e.Info.Id == id)
        ?? throw new StudioException("This endpoint has changed on disk since you opened it. Please refresh and try again.");

    private static string ValidateVerb(string v)
    {
        var verb = (v ?? "").Trim().ToUpperInvariant();
        if (!ProjectAnalyzer.HttpMethods.Contains(verb)) throw new StudioException("Please choose a valid HTTP method.");
        return verb;
    }

    private static string ValidateName(string n)
    {
        var name = (n ?? "").Trim();
        if (name.Length == 0) throw new StudioException("Please give the endpoint a name.");
        if (!IdentifierRx.IsMatch(name) || SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None)
            throw new StudioException("The name can only contain letters, numbers and underscores, and must start with a letter.");
        return name;
    }

    private static string ValidateGroupName(string n)
    {
        var name = ProjectAnalyzer.ToPascal(n ?? "");
        if (name.Length == 0 || !char.IsLetter(name[0])) throw new StudioException("Please give the new group a name that starts with a letter.");
        return name;
    }

    private static string ValidateRoute(string r)
    {
        var route = (r ?? "").Trim();
        if (!RouteRx.IsMatch(route)) throw new StudioException("The path contains characters that aren't allowed. Use letters, numbers, - _ / and {placeholders}.");
        if (route.Count(c => c == '{') != route.Count(c => c == '}')) throw new StudioException("The path has an unclosed { placeholder }.");
        if (route.Contains("//")) throw new StudioException("The path can't contain an empty segment (//).");
        return route;
    }

    private static string RouteShape(string route) =>
        Regex.Replace(route.ToLowerInvariant(), @"\{[^}]*\}", "{}").TrimEnd('/');

    private static void EnsureNoConflict(ProjectModel model, string verb, string fullRoute, EndpointRecord? self)
    {
        var shape = RouteShape(fullRoute);
        var clash = model.Endpoints.FirstOrDefault(e => e != self && e.Info.HttpMethod == verb && RouteShape(e.Info.Route) == shape);
        if (clash is not null)
            throw new StudioException($"There is already a {verb} endpoint at {clash.Info.Route} ({(clash.Info.Name.Length > 0 ? clash.Info.Name : "unnamed")}).");
    }

    private static void EnsureNameAvailable(ProjectModel model, EndpointStyle style, GroupRecord? group, string name, EndpointRecord? self)
    {
        if (style == EndpointStyle.Controller)
        {
            if (group?.Class is { } cls && cls.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == name && m != self?.Method))
                throw new StudioException($"This group already has an endpoint named \"{name}\".");
        }
        else if (model.Endpoints.Any(e => e != self && e.Info.Style == EndpointStyle.MinimalApi && e.Info.Name == name))
        {
            throw new StudioException($"Another endpoint is already named \"{name}\". Names must be unique.");
        }
    }
}
