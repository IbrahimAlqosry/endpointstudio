using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EndpointStudio.Server.Models;
using EndpointStudio.Server.Services;
using Microsoft.CodeAnalysis.CSharp;

namespace EndpointStudio.Server.Codegen;

/// <summary>Creates a brand-new Web API project from a database's tables.</summary>
public class ProjectGenerator(DatabaseService db, EndpointWriter writer, HistoryService history, DotnetService dotnet)
{
    private const string Nl = "\r\n";

    public static class Versions
    {
        public const string Tfm = "net10.0";
        public const string AspNet = "10.0.15";
        public const string EfSqlServer = "10.0.15";
        public const string PomeloMySql = "9.0.0";
        public const string Scalar = "3.2.1";
    }

    private sealed record EntityPlan(DbTable Table, string ClassName, string SetName, List<(DbColumn Column, string Property)> Props, List<string> Operations);

    public async Task<GenerateProjectResult> GenerateAsync(GenerateProjectRequest req, CancellationToken ct)
    {
        var name = (req.ProjectName ?? "").Trim();
        if (!Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_.]*$") || name.EndsWith('.'))
            throw new StudioException("The project name must start with a letter and contain only letters, numbers, dots or underscores.");
        if (string.IsNullOrWhiteSpace(req.Location) || !Directory.Exists(req.Location))
            throw new StudioException("Please choose an existing folder to create the project in.");
        if (req.Tables.Count == 0) throw new StudioException("Please select at least one table.");

        var dir = Path.Combine(req.Location, name);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new StudioException($"The folder \"{name}\" already exists in that location and isn't empty.");

        var allTables = await db.GetTablesAsync(req.Connection, ct);
        var serverVersion = req.Connection.Provider == DbProvider.MySql ? await db.ServerVersionAsync(req.Connection, ct) : null;
        var ns = Regex.Replace(name, @"[^A-Za-z0-9_.]", "_");

        // ---- Plan entities ----
        var plans = new List<EntityPlan>();
        var usedClassNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AppDbContext", "Program", ns.Split('.').Last() };
        foreach (var sel in req.Tables)
        {
            var table = allTables.FirstOrDefault(t => t.Schema == sel.Schema && t.Name == sel.Name)
                        ?? throw new StudioException($"The table {sel.Schema}.{sel.Name} no longer exists.");
            var baseName = Singular(Identifier(table.Name));
            var className = baseName;
            if (!usedClassNames.Add(className)) { className = Identifier(table.Schema) + baseName; usedClassNames.Add(className); }
            var setName = Plural(className);

            var props = new List<(DbColumn, string)>();
            var usedProps = new HashSet<string>(StringComparer.Ordinal) { className };
            foreach (var col in table.Columns.Where(c => c.Supported))
            {
                var p = Identifier(col.Name);
                if (p.Length == 0) continue;
                while (!usedProps.Add(p)) p += "Value";
                props.Add((col, p));
            }
            if (props.Count == 0) continue;

            var keyCount = table.Columns.Count(c => c.IsPrimaryKey && c.Supported);
            var ops = sel.Operations.Select(o => o.ToLowerInvariant()).Distinct().ToList();
            if (keyCount != 1) ops = ops.Where(o => o is "list" || o is "create" && keyCount > 1).ToList();
            if (table.IsView) ops = ops.Where(o => o == "list").ToList();
            plans.Add(new EntityPlan(table, className, setName, props, ops));
        }

        Directory.CreateDirectory(dir);
        var created = new List<string>();
        void Write(string rel, string content)
        {
            var path = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            created.Add(rel.Replace('\\', '/'));
        }

        try
        {
            var csprojPath = Path.Combine(dir, name + ".csproj");
            Write(name + ".csproj", Csproj(req, ns));
            Write("Program.cs", ProgramCs(req, ns, serverVersion));
            Write("appsettings.json", AppSettings(db.ConnectionString(req.Connection, forApp: true)));
            Write(Path.Combine("Properties", "launchSettings.json"), LaunchSettings(req.IncludeApiDocs));
            Write(".gitignore", "bin/" + Nl + "obj/" + Nl + ".vs/" + Nl + "*.user" + Nl);
            Write(Path.Combine("Data", "AppDbContext.cs"), DbContextCs(ns, plans, req.Connection.Provider));
            foreach (var p in plans) Write(Path.Combine("Entities", p.ClassName + ".cs"), EntityCs(ns, p));
            Write("README.md", Readme(name, req, plans));

            // ---- Endpoints, added through the same engine the editor uses ----
            foreach (var p in plans)
            {
                var route = "/api/" + Kebab(p.SetName);
                string? groupKey = null;
                foreach (var op in new[] { "list", "get", "create", "update", "delete" }.Where(p.Operations.Contains))
                {
                    var (verb, rel, behavior, epName, summary) = op switch
                    {
                        "list" => ("GET", "", Behaviors.EntityList, $"Get{p.SetName}", $"Get a page of {Words(p.SetName).ToLowerInvariant()}"),
                        "get" => ("GET", "{id}", Behaviors.EntityGet, $"Get{p.ClassName}ById", $"Get one {Words(p.ClassName).ToLowerInvariant()} by its ID"),
                        "create" => ("POST", "", Behaviors.EntityCreate, $"Create{p.ClassName}", $"Create a new {Words(p.ClassName).ToLowerInvariant()}"),
                        "update" => ("PUT", "{id}", Behaviors.EntityUpdate, $"Update{p.ClassName}", $"Update an existing {Words(p.ClassName).ToLowerInvariant()}"),
                        _ => ("DELETE", "{id}", Behaviors.EntityDelete, $"Delete{p.ClassName}", $"Delete a {Words(p.ClassName).ToLowerInvariant()}"),
                    };
                    var add = new AddEndpointRequest(
                        GroupKey: groupKey,
                        NewGroup: groupKey is null ? new NewGroupRequest(p.SetName, req.Style, route) : null,
                        HttpMethod: verb, Route: rel, Name: epName, Summary: summary, RequiresAuth: false,
                        Behavior: behavior, Message: null, Entity: p.ClassName, FilterProperty: null, Parameters: null);
                    writer.Add(csprojPath, add);
                    if (groupKey is null)
                    {
                        var model = ProjectAnalyzer.Analyze(csprojPath);
                        groupKey = model.Groups.First(g => g.BaseRoute.Equals(route, StringComparison.OrdinalIgnoreCase)).Key;
                    }
                }
            }
            history.Clear(csprojPath); // a fresh project starts with a clean history

            created = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).OrderBy(f => f).ToList();

            BuildResult? build = null;
            try { build = await dotnet.BuildAsync(csprojPath, ct); }
            catch (StudioException) { /* build is a best-effort check */ }
            return new GenerateProjectResult(csprojPath, created, build);
        }
        catch
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
            throw;
        }
    }

    // ------------------------------------------------------------------ Templates

    private static string Csproj(GenerateProjectRequest req, string ns)
    {
        var sb = new StringBuilder();
        sb.Append("<Project Sdk=\"Microsoft.NET.Sdk.Web\">").Append(Nl).Append(Nl);
        sb.Append("  <PropertyGroup>").Append(Nl);
        sb.Append($"    <TargetFramework>{Versions.Tfm}</TargetFramework>").Append(Nl);
        sb.Append("    <Nullable>enable</Nullable>").Append(Nl);
        sb.Append("    <ImplicitUsings>enable</ImplicitUsings>").Append(Nl);
        sb.Append($"    <RootNamespace>{ns}</RootNamespace>").Append(Nl);
        sb.Append("  </PropertyGroup>").Append(Nl).Append(Nl);
        sb.Append("  <ItemGroup>").Append(Nl);
        sb.Append($"    <PackageReference Include=\"Microsoft.AspNetCore.OpenApi\" Version=\"{Versions.AspNet}\" />").Append(Nl);
        if (req.Connection.Provider == DbProvider.SqlServer)
            sb.Append($"    <PackageReference Include=\"Microsoft.EntityFrameworkCore.SqlServer\" Version=\"{Versions.EfSqlServer}\" />").Append(Nl);
        else
            sb.Append($"    <PackageReference Include=\"Pomelo.EntityFrameworkCore.MySql\" Version=\"{Versions.PomeloMySql}\" />").Append(Nl);
        if (req.IncludeApiDocs)
            sb.Append($"    <PackageReference Include=\"Scalar.AspNetCore\" Version=\"{Versions.Scalar}\" />").Append(Nl);
        sb.Append("  </ItemGroup>").Append(Nl).Append(Nl);
        sb.Append("</Project>").Append(Nl);
        return sb.ToString();
    }

    private static string ProgramCs(GenerateProjectRequest req, string ns, string? mysqlVersion)
    {
        var l = new List<string> { "using Microsoft.EntityFrameworkCore;" };
        if (req.IncludeApiDocs) l.Add("using Scalar.AspNetCore;");
        l.Add($"using {ns}.Data;");
        l.Add("");
        l.Add("var builder = WebApplication.CreateBuilder(args);");
        l.Add("");
        l.Add("// Database");
        l.Add("var connectionString = builder.Configuration.GetConnectionString(\"Default\");");
        if (req.Connection.Provider == DbProvider.SqlServer)
        {
            l.Add("builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));");
        }
        else
        {
            var v = Regex.Match(mysqlVersion ?? "8.0.0", @"\d+\.\d+\.\d+").Value;
            if (v.Length == 0) v = "8.0.0";
            var kind = (mysqlVersion ?? "").Contains("mariadb", StringComparison.OrdinalIgnoreCase) ? "MariaDbServerVersion" : "MySqlServerVersion";
            l.Add($"builder.Services.AddDbContext<AppDbContext>(options => options.UseMySql(connectionString, new {kind}(new Version({v.Replace(".", ", ")}))));");
        }
        l.Add("");
        l.Add("// API");
        if (req.Style == EndpointStyle.Controller) l.Add("builder.Services.AddControllers();");
        l.Add("builder.Services.AddOpenApi();");
        l.Add("builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));");
        l.Add("");
        l.Add("var app = builder.Build();");
        l.Add("");
        l.Add("app.MapOpenApi();");
        if (req.IncludeApiDocs)
        {
            l.Add("app.MapScalarApiReference();");
            l.Add("app.MapGet(\"/\", () => Results.Redirect(\"/scalar\")).ExcludeFromDescription();");
        }
        l.Add("app.UseCors();");
        if (req.Style == EndpointStyle.Controller) l.Add("app.MapControllers();");
        l.Add("");
        l.Add("app.Run();");
        return string.Join(Nl, l) + Nl;
    }

    private static string AppSettings(string connectionString) =>
        JsonSerializer.Serialize(new
        {
            ConnectionStrings = new { Default = connectionString },
            Logging = new { LogLevel = new Dictionary<string, string> { ["Default"] = "Information", ["Microsoft.AspNetCore"] = "Warning" } },
            AllowedHosts = "*",
        }, new JsonSerializerOptions { WriteIndented = true }).Replace("\n", Nl) + Nl;

    private static string LaunchSettings(bool docs)
    {
        var port = Random.Shared.Next(5100, 5900);
        var obj = new Dictionary<string, object>
        {
            ["profiles"] = new Dictionary<string, object>
            {
                ["http"] = new Dictionary<string, object?>
                {
                    ["commandName"] = "Project",
                    ["dotnetRunMessages"] = true,
                    ["launchBrowser"] = true,
                    ["launchUrl"] = docs ? "scalar" : "openapi/v1.json",
                    ["applicationUrl"] = $"http://localhost:{port}",
                    ["environmentVariables"] = new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Development" },
                },
            },
        };
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }).Replace("\n", Nl) + Nl;
    }

    private static string DbContextCs(string ns, List<EntityPlan> plans, DbProvider provider)
    {
        var l = new List<string>
        {
            "using Microsoft.EntityFrameworkCore;",
            $"using {ns}.Entities;",
            "",
            $"namespace {ns}.Data;",
            "",
            "public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)",
            "{",
        };
        foreach (var p in plans) l.Add($"    public DbSet<{p.ClassName}> {p.SetName} => Set<{p.ClassName}>();");
        l.Add("");
        l.Add("    protected override void OnModelCreating(ModelBuilder modelBuilder)");
        l.Add("    {");
        foreach (var p in plans)
        {
            // For MySQL the "schema" is the database itself, so it isn't passed.
            var schemaArg = provider == DbProvider.SqlServer && p.Table.Schema.Length > 0 ? $", {Lit(p.Table.Schema)}" : "";
            var keys = p.Props.Where(x => x.Column.IsPrimaryKey).Select(x => x.Property).ToList();
            var chain = new List<string>();
            chain.Add(p.Table.IsView ? $".ToView({Lit(p.Table.Name)}{schemaArg})" : $".ToTable({Lit(p.Table.Name)}{schemaArg})");
            if (keys.Count == 0) chain.Add(".HasNoKey()");
            else if (keys.Count > 1) chain.Add($".HasKey(e => new {{ {string.Join(", ", keys.Select(k => "e." + k))} }})");
            l.Add($"        modelBuilder.Entity<{p.ClassName}>(){string.Concat(chain)};");
        }
        l.Add("    }");
        l.Add("}");
        return string.Join(Nl, l) + Nl;
    }

    private static string EntityCs(string ns, EntityPlan p)
    {
        var l = new List<string>
        {
            "using System.ComponentModel.DataAnnotations;",
            "using System.ComponentModel.DataAnnotations.Schema;",
            "",
            $"namespace {ns}.Entities;",
            "",
            $"/// <summary>A row in the {p.Table.Name} {(p.Table.IsView ? "view" : "table")}.</summary>",
            $"public class {p.ClassName}",
            "{",
        };
        var singleKey = p.Props.Count(x => x.Column.IsPrimaryKey) == 1;
        var first = true;
        foreach (var (col, prop) in p.Props)
        {
            if (!first) l.Add("");
            first = false;
            if (col.IsPrimaryKey && singleKey) l.Add("    [Key]");
            if (col.IsPrimaryKey && singleKey)
            {
                if (col.IsIdentity) l.Add("    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]");
                else if (col.ClrType is "int" or "long" or "short" or "byte" or "uint" or "ulong") l.Add("    [DatabaseGenerated(DatabaseGeneratedOption.None)]");
            }
            if (col.IsComputed)
            {
                if (col.SqlType is "timestamp" or "rowversion") l.Add("    [Timestamp]");
                else l.Add("    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]");
            }
            if (prop != col.Name) l.Add($"    [Column({Lit(col.Name)})]");
            if (col.ClrType == "string" && col.MaxLength is > 0) l.Add($"    [MaxLength({col.MaxLength})]");
            var isRef = col.ClrType is "string" or "byte[]";
            var nullable = col.Nullable || col.IsComputed && isRef;
            var type = col.ClrType + (nullable ? "?" : "");
            var init = isRef && !nullable ? (col.ClrType == "string" ? " = string.Empty;" : " = [];") : "";
            l.Add($"    public {type} {prop} {{ get; set; }}{init}");
        }
        l.Add("}");
        return string.Join(Nl, l) + Nl;
    }

    private static string Readme(string name, GenerateProjectRequest req, List<EntityPlan> plans)
    {
        var sb = new StringBuilder();
        sb.Append($"# {name}").Append(Nl).Append(Nl);
        sb.Append($"Web API generated by Endpoint Studio from the `{req.Connection.Database}` database ").Append(req.Connection.Provider == DbProvider.SqlServer ? "(SQL Server)." : "(MySQL).").Append(Nl).Append(Nl);
        sb.Append("## Run").Append(Nl).Append(Nl).Append("```").Append(Nl).Append("dotnet run").Append(Nl).Append("```").Append(Nl).Append(Nl);
        if (req.IncludeApiDocs) sb.Append("Interactive API docs are served at `/scalar`.").Append(Nl).Append(Nl);
        sb.Append("## Resources").Append(Nl).Append(Nl);
        foreach (var p in plans)
            sb.Append($"- `/api/{Kebab(p.SetName)}` → `{p.Table.Schema}.{p.Table.Name}` ({string.Join(", ", p.Operations)})").Append(Nl);
        sb.Append(Nl).Append("> The connection string is stored in `appsettings.json`. Move secrets to user-secrets or environment variables before sharing this project.").Append(Nl);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Naming helpers

    private static string Lit(string s) => SymbolDisplay.FormatLiteral(s, true);

    public static string Identifier(string raw)
    {
        var pascal = ProjectAnalyzer.ToPascal(raw);
        if (pascal.Length == 0) return "";
        if (char.IsDigit(pascal[0])) pascal = "N" + pascal;
        return pascal;
    }

    private static string Singular(string s)
    {
        if (s.EndsWith("ies") && s.Length > 4) return s[..^3] + "y";
        if (s.EndsWith("sses") || s.EndsWith("xes") || s.EndsWith("ches") || s.EndsWith("shes")) return s[..^2];
        if (s.EndsWith("ss") || s.EndsWith("us") || s.EndsWith("is")) return s;
        if (s.EndsWith('s') && s.Length > 3) return s[..^1];
        return s;
    }

    private static string Plural(string s)
    {
        if (s.EndsWith('y') && s.Length > 1 && !"aeiou".Contains(char.ToLowerInvariant(s[^2]))) return s[..^1] + "ies";
        if (s.EndsWith('s') || s.EndsWith('x') || s.EndsWith("ch") || s.EndsWith("sh")) return s + "es";
        return s + "s";
    }

    private static string Kebab(string s) => Regex.Replace(s, "(?<=[a-z0-9])(?=[A-Z])", "-").ToLowerInvariant();
    private static string Words(string s) => ProjectAnalyzer.SplitWords(s);
}
