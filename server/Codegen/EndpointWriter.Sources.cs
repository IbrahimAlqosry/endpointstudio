using System.Text.Json;
using System.Text.RegularExpressions;
using Stackwright.Server.Models;
using Stackwright.Server.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Stackwright.Server.Codegen;

/// <summary>
/// Endpoints that get their data the way the project already does: through a service, with direct SQL,
/// or by copying an existing endpoint's pattern. None of these need Entity Framework.
/// </summary>
public partial class EndpointWriter
{
    private static readonly HashSet<string> SimpleTypeNames = new(StringComparer.Ordinal)
    {
        "string", "int", "long", "short", "byte", "bool", "decimal", "double", "float", "Guid", "DateTime", "DateTimeOffset",
        "DateOnly", "TimeOnly", "TimeSpan", "char", "uint", "ulong", "ushort", "sbyte", "String", "Int32", "Int64", "Boolean", "Decimal", "Double",
    };

    // ================================================================== Call a service operation

    private static void BuildServiceCall(ProjectModel model, AddEndpointRequest req, Plan plan, HashSet<string> routeParams, GroupRecord? group)
    {
        var svc = model.Services.FirstOrDefault(s => s.Interface == req.ServiceInterface)
                  ?? throw new StudioException("Please choose which service to call.");
        var op = svc.Operations.FirstOrDefault(o => o.Name == req.ServiceOperation)
                 ?? throw new StudioException("Please choose which operation to call.");
        plan.Usings.Add(svc.Namespace);
        foreach (var u in UsingsOfType(model, svc.Interface)) plan.Usings.Add(u);

        // Use the service the controller already receives, or ask for it in the endpoint
        var svcVar = group?.Class is { } cls ? FindMemberOfType(cls, svc.Interface) : null;
        if (svcVar is null)
        {
            if (model.IsFramework)
                throw new StudioException($"This controller doesn't receive {svc.Interface} yet. Add the endpoint to a group whose controller already uses it, or copy an existing endpoint that calls it.");
            svcVar = char.ToLowerInvariant(svc.Interface[1]) + svc.Interface[2..];
            if (op.Parameters.Any(p => p.Name == svcVar)) svcVar += "Service";
            plan.Params.Add(new GenParam(svcVar, svc.Interface, ParamSource.Service, false));
        }

        var args = new List<string>();
        var bodies = 0;
        foreach (var p in op.Parameters)
        {
            args.Add(p.Name);
            if (p.Injected)
            {
                if (model.IsFramework) throw new StudioException($"{op.Name} needs {p.Type}, which .NET Framework controllers can't receive automatically. Copy an existing endpoint that calls it instead.");
                plan.Params.Add(new GenParam(p.Name, p.Type, ParamSource.Service, false));
                continue;
            }
            var simple = SimpleTypeNames.Contains(p.Type.TrimEnd('?'));
            var source = routeParams.Contains(p.Name) ? ParamSource.Route : simple ? ParamSource.Query : ParamSource.Body;
            if (source == ParamSource.Body && ++bodies > 1)
                throw new StudioException($"{op.Name} needs more than one object, so it can't be called from a single request. Copy an existing endpoint that calls it instead.");
            plan.Params.Add(new GenParam(p.Name, p.Type, source, false));
        }
        foreach (var rp in routeParams.Where(rp => op.Parameters.All(p => p.Name != rp)))
            throw new StudioException($"The path placeholder {{{rp}}} isn't one of {op.Name}'s inputs ({string.Join(", ", op.Parameters.Where(p => !p.Injected).Select(p => p.Name))}).");

        var call = $"{svcVar}.{op.Name}({string.Join(", ", args)})";
        var ret = op.ReturnType.Replace(" ", "");
        if (op.IsAsync)
        {
            plan.Async = true;
            if (ret is "Task" or "ValueTask") { plan.Body.Add($"await {call};"); plan.Body.Add("return @NoContent();"); }
            else { plan.Body.Add($"var result = await {call};"); plan.Body.Add("return @Ok(result);"); }
        }
        else if (ret == "void") { plan.Body.Add($"{call};"); plan.Body.Add("return @NoContent();"); }
        else { plan.Body.Add($"var result = {call};"); plan.Body.Add("return @Ok(result);"); }
    }

    /// <summary>Name of a field, property or constructor parameter of the given type in a class.</summary>
    private static string? FindMemberOfType(ClassDeclarationSyntax cls, string typeName)
    {
        foreach (var f in cls.Members.OfType<FieldDeclarationSyntax>())
            if (ProjectAnalyzer.SimpleName(f.Declaration.Type) == typeName) return f.Declaration.Variables.First().Identifier.Text;
        foreach (var p in cls.Members.OfType<PropertyDeclarationSyntax>())
            if (ProjectAnalyzer.SimpleName(p.Type) == typeName) return p.Identifier.Text;
        if (cls.ParameterList is { } primary) // C# 12 primary constructor
            foreach (var p in primary.Parameters)
                if (p.Type is not null && ProjectAnalyzer.SimpleName(p.Type) == typeName) return p.Identifier.Text;
        return null;
    }

    /// <summary>The using directives of the file that declares a type (so its parameter types resolve in the endpoint's file).</summary>
    private static IEnumerable<string> UsingsOfType(ProjectModel model, string typeName)
    {
        var file = model.Files.FirstOrDefault(f => f.Root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Any(t => t.Identifier.Text == typeName));
        return file is null ? [] : file.Root.Usings.Where(u => u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None)).Select(u => u.Name?.ToString() ?? "").Where(n => n.Length > 0);
    }

    // ================================================================== Run SQL

    private sealed record SqlDriver(string Namespace, string ConnectionClass, string ParamPrefix, bool BindByName, string Package, string Version);

    private static SqlDriver DriverFor(ProjectModel model, string provider) => provider switch
    {
        "Oracle" => new("Oracle.ManagedDataAccess.Client", "OracleConnection", "", true, "Oracle.ManagedDataAccess.Core", "23.26.401"),
        "PostgreSql" => new("Npgsql", "NpgsqlConnection", "@", false, "Npgsql", "8.0.5"),
        "MySql" => model.AllPackages.Contains("MySql.Data") && !model.AllPackages.Contains("MySqlConnector") && !model.AllPackages.Contains("Pomelo.EntityFrameworkCore.MySql")
            ? new("MySql.Data.MySqlClient", "MySqlConnection", "@", false, "MySql.Data", "9.1.0")
            : new("MySqlConnector", "MySqlConnection", "@", false, "MySqlConnector", "2.4.0"),
        "Sqlite" => new("Microsoft.Data.Sqlite", "SqliteConnection", "@", false, "Microsoft.Data.Sqlite", "8.0.11"),
        "SqlServer" => model.AllPackages.Contains("Microsoft.Data.SqlClient") || model.AllPackages.Contains("Microsoft.EntityFrameworkCore.SqlServer") || !(model.IsFramework || model.AllPackages.Contains("System.Data.SqlClient"))
            ? new("Microsoft.Data.SqlClient", "SqlConnection", "@", false, "Microsoft.Data.SqlClient", "5.2.2")
            : new("System.Data.SqlClient", "SqlConnection", "@", false, "System.Data.SqlClient", "4.8.6"),
        _ => throw new StudioException("Please choose which kind of database the SQL runs on."),
    };

    private static readonly Regex SqlParamRx = new(@"(?<![:@\w])[:@]([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    /// <summary>Parameter names used in a SQL statement (@name or :name), ignoring text inside quotes and comments.</summary>
    public static List<string> SqlParameterNames(string sql)
    {
        var clean = Regex.Replace(sql, @"'([^']|'')*'|--[^\n]*|/\*.*?\*/", " ", RegexOptions.Singleline);
        return SqlParamRx.Matches(clean).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void BuildSql(ProjectModel model, AddEndpointRequest req, Plan plan, HashSet<string> routeParams, string verb)
    {
        var provider = req.SqlProvider ?? model.SqlProviders.FirstOrDefault()
                       ?? throw new StudioException("Please choose which kind of database the SQL runs on.");
        var driver = DriverFor(model, provider);
        if (!model.SqlProviders.Contains(provider))
        {
            if (model.IsFramework || model.IsLegacyProject)
                throw new StudioException("This project doesn't reference a driver for that database yet. Add it in Visual Studio first (NuGet), then try again.");
            plan.AddPackage = (driver.Package, driver.Version);
        }

        var connName = (req.ConnectionName ?? "").Trim();
        if (!Regex.IsMatch(connName, @"^[A-Za-z0-9_.\-]+$")) throw new StudioException("Please choose or enter the name of the connection string.");
        if (!model.ConnectionStrings.Contains(connName))
        {
            if (string.IsNullOrWhiteSpace(req.ConnectionString))
                throw new StudioException($"There's no connection string called “{connName}” in the settings yet. Enter its value so it can be added.");
            if (model.IsFramework) throw new StudioException("Add the connection string to Web.config first, then try again.");
            plan.AddConnectionString = (connName, req.ConnectionString.Trim());
        }

        var sql = (req.Sql ?? "").Trim();
        if (sql.Length == 0) throw new StudioException(req.SqlKind == "procedure" ? "Please enter the stored procedure name." : "Please enter the SQL to run.");
        var isProc = req.SqlKind == "procedure";
        if (isProc && !Regex.IsMatch(sql, @"^[A-Za-z_][\w.$#]*$")) throw new StudioException("The stored procedure name can only contain letters, numbers, dots and underscores.");

        var userParams = (req.Parameters ?? []).Where(p => p.Source != ParamSource.Service).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var names = isProc ? userParams.Keys.ToList() : SqlParameterNames(sql);
        foreach (var n in names)
        {
            if (!IdentifierRx.IsMatch(n) || SyntaxFacts.GetKeywordKind(n) != SyntaxKind.None || n is "connection" or "command" or "reader" or "rows" or "row" or "configuration" or "connectionString")
                throw new StudioException($"The SQL parameter “{n}” can't be used as an input name. Rename it in the SQL.");
            userParams.TryGetValue(n, out var up);
            var type = up is null ? "string" : ResolveParamType(model, up.Type, plan);
            if (type is "JsonElement" or "JObject" || model.Entities.Any(e => e.Name == type)) throw new StudioException($"SQL parameter “{n}” must be a simple value (text, number, date…).");
            var source = routeParams.Contains(n) ? ParamSource.Route : up?.Source is ParamSource.Header ? ParamSource.Header : ParamSource.Query;
            if (source == ParamSource.Header && model.IsFramework) throw new StudioException(".NET Framework Web API can't read inputs from headers directly.");
            plan.Params.Add(new GenParam(n, type, source, source != ParamSource.Route && up?.Optional == true));
        }
        foreach (var rp in routeParams.Where(rp => !names.Contains(rp, StringComparer.OrdinalIgnoreCase)))
            throw new StudioException($"The path placeholder {{{rp}}} isn't used in the SQL. Use it as a parameter (for example @{rp}) or remove it from the path.");

        plan.Usings.Add("System");
        plan.Usings.Add("System.Collections.Generic");
        plan.Usings.Add("System.Data");
        plan.Usings.Add(driver.Namespace);

        var commandText = "@\"" + sql.Replace("\"", "\"\"") + "\"";
        var paramLines = names.SelectMany(n => new[]
        {
            $"var {n}Parameter = command.CreateParameter();",
            $"{n}Parameter.ParameterName = \"{driver.ParamPrefix}{n}\";",
            $"{n}Parameter.Value = (object){n} ?? DBNull.Value;",
            $"command.Parameters.Add({n}Parameter);",
        }).ToList();
        var readRows = new[]
        {
            "var rows = new List<Dictionary<string, object>>();",
            "while (@Read)",
            "{",
            "    var row = new Dictionary<string, object>();",
            "    for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);",
            "    rows.Add(row);",
            "}",
            "return @Ok(rows);",
        };

        string connectionLine;
        if (model.IsFramework)
        {
            plan.Usings.Add("System.Configuration");
            connectionLine = $"var connectionString = ConfigurationManager.ConnectionStrings[\"{connName}\"].ConnectionString;";
        }
        else
        {
            plan.Usings.Add("Microsoft.Extensions.Configuration");
            plan.Params.Add(new GenParam("configuration", "IConfiguration", ParamSource.Service, false));
            connectionLine = $"var connectionString = configuration.GetConnectionString(\"{connName}\");";
        }

        var modernAsync = !model.IsFramework && (model.FrameworkVersion == 0 || model.FrameworkVersion >= 3);
        plan.Body.Add(connectionLine);
        if (modernAsync)
        {
            plan.Async = true;
            plan.Body.Add($"await using var connection = new {driver.ConnectionClass}(connectionString);");
            plan.Body.Add("await connection.OpenAsync();");
            plan.Body.Add("await using var command = connection.CreateCommand();");
            if (driver.BindByName) plan.Body.Add("command.BindByName = true;");
            plan.Body.Add($"command.CommandText = {commandText};");
            if (isProc) plan.Body.Add("command.CommandType = CommandType.StoredProcedure;");
            plan.Body.AddRange(paramLines);
            plan.Body.Add("await using var reader = await command.ExecuteReaderAsync();");
            plan.Body.Add("if (reader.FieldCount == 0) return @Ok(new { affectedRows = reader.RecordsAffected });");
            plan.Body.AddRange(readRows.Select(l => l.Replace("@Read", "await reader.ReadAsync()")));
        }
        else
        {
            // C# 7.3 (.NET Framework, .NET Core 2): classic using blocks
            plan.Body.Add($"using (var connection = new {driver.ConnectionClass}(connectionString))");
            plan.Body.Add("{");
            plan.Body.Add("    connection.Open();");
            plan.Body.Add("    using (var command = connection.CreateCommand())");
            plan.Body.Add("    {");
            if (driver.BindByName) plan.Body.Add("        command.BindByName = true;");
            plan.Body.Add($"        command.CommandText = {commandText};");
            if (isProc) plan.Body.Add("        command.CommandType = CommandType.StoredProcedure;");
            plan.Body.AddRange(paramLines.Select(l => "        " + l));
            plan.Body.Add("        using (var reader = command.ExecuteReader())");
            plan.Body.Add("        {");
            plan.Body.Add("            if (reader.FieldCount == 0) return @Ok(new { affectedRows = reader.RecordsAffected });");
            plan.Body.AddRange(readRows.Select(l => "            " + l.Replace("@Read", "reader.Read()")));
            plan.Body.Add("        }");
            plan.Body.Add("    }");
            plan.Body.Add("}");
        }
        _ = verb;
    }

    private static void AddPackageReference(ProjectModel model, HistoryService.ChangeSet changes, string id, string version)
    {
        var text = changes.Read(model.CsprojPath);
        if (Regex.IsMatch(text, $@"Include=""{Regex.Escape(id)}""", RegexOptions.IgnoreCase)) return;
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var last = Regex.Matches(text, @"^([ \t]*)<PackageReference\b[^>]*?(/>|>.*?</PackageReference>)[ \t]*\r?\n", RegexOptions.Multiline | RegexOptions.Singleline).LastOrDefault();
        var updated = last is not null
            ? text.Insert(last.Index + last.Length, $"{last.Groups[1].Value}<PackageReference Include=\"{id}\" Version=\"{version}\" />{nl}")
            : text.Insert(text.LastIndexOf("</Project>", StringComparison.Ordinal), $"  <ItemGroup>{nl}    <PackageReference Include=\"{id}\" Version=\"{version}\" />{nl}  </ItemGroup>{nl}{nl}");
        System.Xml.Linq.XDocument.Parse(updated);
        changes.Write(model.CsprojPath, updated);
    }

    private static void AddConnectionString(ProjectModel model, HistoryService.ChangeSet changes, string name, string value)
    {
        var path = Path.Combine(model.Directory, "appsettings.json");
        var entry = $"{JsonSerializer.Serialize(name)}: {JsonSerializer.Serialize(value)}";
        if (!changes.Exists(path))
        {
            changes.Write(path, "{\r\n  \"ConnectionStrings\": {\r\n    " + entry + "\r\n  }\r\n}\r\n");
            return;
        }
        var text = changes.Read(path);
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var section = Regex.Match(text, @"""ConnectionStrings""\s*:\s*\{");
        string updated;
        if (section.Success)
        {
            var after = section.Index + section.Length;
            var empty = Regex.IsMatch(text[after..], @"^\s*\}");
            updated = text.Insert(after, $"{nl}    {entry}{(empty ? "" : ",")}");
        }
        else
        {
            var brace = text.IndexOf('{');
            if (brace < 0) throw new StudioException("appsettings.json couldn't be read, so the connection string wasn't added.");
            var empty = Regex.IsMatch(text[(brace + 1)..], @"^\s*\}");
            updated = text.Insert(brace + 1, $"{nl}  \"ConnectionStrings\": {{{nl}    {entry}{nl}  }}{(empty ? "" : ",")}");
        }
        try { JsonDocument.Parse(updated, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch { throw new StudioException("appsettings.json has an unusual layout, so the connection string couldn't be added safely. Add it by hand and try again."); }
        changes.Write(path, updated);
    }

    // ================================================================== Copy an existing endpoint

    private string CloneEndpoint(ProjectModel model, AddEndpointRequest req, string verb, string route, string name)
    {
        var tpl = Find(model, req.TemplateEndpointId ?? "");
        var group = tpl.Group;
        var fullRoute = ProjectAnalyzer.CombineRoutes(group.BaseRoute, route);
        EnsureNoConflict(model, verb, fullRoute, null);
        EnsureNameAvailable(model, tpl.Info.Style, group, name, null);

        string? svcName = null, oldOp = null;
        if (tpl.Info.Calls is { } calls && calls.Split('.') is [var s, var o]) { svcName = s; oldOp = o; }
        string? newOp = null;
        var wanted = (req.ServiceOperation ?? "").Trim();
        if (wanted.Length > 0 && oldOp is not null && wanted != oldOp)
        {
            newOp = ValidateName(wanted);
            var svc = model.Services.First(x => x.Interface == svcName);
            var exists = svc.Operations.Any(x => x.Name == newOp);
            if (req.NewOperation && exists) throw new StudioException($"{svc.Interface} already has an operation called {newOp}. Pick it from the list instead.");
            if (!req.NewOperation && !exists) throw new StudioException($"{svc.Interface} has no operation called {newOp}.");
        }

        var changes = History.Begin(model.CsprojPath, $"Added {verb} {fullRoute} (copy of {tpl.Info.Name})");
        if (req.NewOperation && newOp is not null) AddServiceOperation(model, changes, svcName!, oldOp!, newOp);

        var update = new UpdateEndpointRequest(verb, route, name, string.IsNullOrWhiteSpace(req.Summary) ? tpl.Info.Summary : req.Summary, req.RequiresAuth);
        var edits = new TextEdits(changes.Read(tpl.File.Path));
        if (edits.Original != tpl.File.Text) throw new StudioException("The endpoint's file changed while copying. Please try again.");

        if (tpl.Info.Style == EndpointStyle.Controller)
        {
            var cls = group.Class!;
            var m = tpl.Method!;
            var header = string.Concat(cls.AttributeLists.Select(a => a.ToString() + "\n")) + $"public class {cls.Identifier.Text} {cls.BaseList}\n{{\n";
            var wrapper = header + m.ToFullString() + "\n}\n";
            var copy = ProjectAnalyzer.ParseEndpoints(model, tpl.File.Relative, wrapper).FirstOrDefault(r => r.Method?.Identifier.Text == m.Identifier.Text)
                       ?? throw new StudioException("This endpoint couldn't be copied.");
            var w = new TextEdits(wrapper);
            UpdateController(model, copy, w, verb, route, name, update);
            SwapCalls(w, (SyntaxNode?)copy.Method!.Body ?? copy.Method.ExpressionBody, oldOp, newOp, tpl.Info.Name, name);
            var result = w.Apply();
            var start = result.IndexOf("\n{\n", result.IndexOf("public class ", StringComparison.Ordinal), StringComparison.Ordinal) + 3;
            var methodText = result[start..result.LastIndexOf("\n}\n", StringComparison.Ordinal)];
            if (!methodText.StartsWith('\n') && !methodText.StartsWith("\r\n")) methodText = edits.NewLine + methodText;
            if (!methodText.EndsWith('\n')) methodText += edits.NewLine;
            edits.Insert(m.FullSpan.End, methodText.Replace("\r\n", "\n").Replace("\n", edits.NewLine));
            if (req.RequiresAuth && !tpl.Info.RequiresAuth) edits.AddUsings(tpl.File.Root, [AuthNamespace(model)], null);
        }
        else
        {
            var stmt = tpl.OuterCall!.Parent as ExpressionStatementSyntax ?? throw new StudioException("This endpoint is written in a way that can't be copied automatically.");
            SyntaxNode anchor = stmt.Parent is GlobalStatementSyntax g ? g : stmt;
            const string header = "class __Copy__\n{\n    void __Run__()\n    {\n";
            var wrapper = header + anchor.ToFullString() + "\n    }\n}\n";
            var copy = ProjectAnalyzer.ParseEndpoints(model, tpl.File.Relative, wrapper).FirstOrDefault()
                       ?? throw new StudioException("This endpoint couldn't be copied.");
            var w = new TextEdits(wrapper);
            UpdateMinimal(model, copy, w, verb, route, name, update);
            var handler = copy.MapCall!.ArgumentList.Arguments.Last().Expression;
            SwapCalls(w, handler, oldOp, newOp, tpl.Info.Name, name);
            var result = w.Apply();
            var start = result.IndexOf(header, StringComparison.Ordinal) + header.Length;
            var text = result[start..result.LastIndexOf("\n    }\n}\n", StringComparison.Ordinal)];
            if (!text.StartsWith('\n') && !text.StartsWith("\r\n")) text = edits.NewLine + text;
            if (!text.EndsWith('\n')) text += edits.NewLine;
            edits.Insert(anchor.FullSpan.End, text.Replace("\r\n", "\n").Replace("\n", edits.NewLine));
        }

        Save(changes, tpl.File, edits);
        if (model.IsFramework) EnsureAttributeRouting(model, changes);
        changes.Commit();
        return $"{verb} {fullRoute} was added, following the pattern of {tpl.Info.Name}" + (req.NewOperation && newOp is not null ? $". A new operation {newOp} was added to {svcName} for a developer to fill in." : ".");
    }

    /// <summary>In a copied endpoint: call the new service operation and replace the old endpoint name in messages.</summary>
    private static void SwapCalls(TextEdits edits, SyntaxNode? body, string? oldOp, string? newOp, string oldName, string newName)
    {
        if (body is null) return;
        if (oldOp is not null && newOp is not null)
            foreach (var inv in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                if (inv.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == oldOp)
                    edits.Replace(ma.Name.Identifier.SpanStart, ma.Name.Identifier.Span.End, newOp);
        foreach (var lit in body.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
            if (string.Equals(lit.Token.ValueText, oldName, StringComparison.OrdinalIgnoreCase))
                edits.Replace(lit.SpanStart, lit.Span.End, TextEdits.Literal(newName));
    }

    /// <summary>Adds an operation to a service interface (same shape as an existing one) and a placeholder in its implementation.</summary>
    private static void AddServiceOperation(ProjectModel model, HistoryService.ChangeSet changes, string interfaceName, string likeOp, string newOp)
    {
        var ifaceFile = model.Files.First(f => f.Root.DescendantNodes().OfType<InterfaceDeclarationSyntax>().Any(i => i.Identifier.Text == interfaceName));
        var iface = ifaceFile.Root.DescendantNodes().OfType<InterfaceDeclarationSyntax>().First(i => i.Identifier.Text == interfaceName);
        var decl = iface.Members.OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == likeOp);

        var e1 = new TextEdits(changes.Read(ifaceFile.Path));
        var full = decl.ToFullString();
        var offset = decl.Identifier.SpanStart - decl.FullSpan.Start;
        var renamed = full[..offset] + newOp + full[(offset + likeOp.Length)..];
        renamed = string.Join("\n", renamed.Split('\n').Where(l => !l.TrimStart().StartsWith("///")));
        e1.Insert(decl.FullSpan.End, renamed);
        Save(changes, ifaceFile, e1);

        var impl = model.Files.SelectMany(f => f.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(c => (File: f, Class: c)))
            .FirstOrDefault(x => x.Class.BaseList?.Types.Any(t => ProjectAnalyzer.SimpleName(t.Type) == interfaceName) == true);
        if (impl.Class is null) return;
        var e2 = new TextEdits(changes.Read(impl.File.Path));
        var nl = e2.NewLine;
        var like = impl.Class.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == likeOp);
        var ind = like is not null ? e2.IndentAt(like.SpanStart) : e2.IndentAt(impl.Class.SpanStart) + "    ";
        var stub = $"{nl}{ind}public {decl.ReturnType} {newOp}{decl.ParameterList}{nl}{ind}{{{nl}{ind}    throw new NotImplementedException(\"{newOp} has not been written yet.\");{nl}{ind}}}{nl}";
        if (like is not null) e2.Insert(like.FullSpan.End, stub);
        else e2.Insert(e2.LineStart(impl.Class.CloseBraceToken.SpanStart), stub);
        if (!model.ImplicitUsings) e2.AddUsings(impl.File.Root, ["System"], null);
        Save(changes, impl.File, e2);
    }
}
