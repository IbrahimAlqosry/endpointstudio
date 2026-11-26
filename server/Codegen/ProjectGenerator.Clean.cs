using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stackwright.Server.Models;

namespace Stackwright.Server.Codegen;

/// <summary>
/// Clean Architecture solution: Domain (entities) ← Application (use cases, DTOs, interfaces) ← Infrastructure (EF Core, JWT) ← Api (HTTP).
/// Each layer only depends on the ones to its left.
/// </summary>
public partial class ProjectGenerator
{
    private sealed record CleanResult(string SolutionPath, string ApiProject, string AdminUser, string? AdminPassword);

    private static string Norm(string s) => s.Replace("\r\n", "\n").Replace("\n", Nl);

    private CleanResult GenerateClean(GenerateProjectRequest req, string dir, string name, string ns, TargetPackages target, string? mysqlVersion, List<EntityPlan> plans, Action<string, string> write)
    {
        var jwt = req.Jwt;
        var minimal = req.Style == EndpointStyle.MinimalApi;
        var domain = $"{name}.Domain";
        var application = $"{name}.Application";
        var infrastructure = $"{name}.Infrastructure";
        var apiName = $"{name}.Api";
        string P(string project, string rel) => Path.Combine("src", project, rel);

        // ---------------- Domain
        write(P(domain, domain + ".csproj"), LibraryCsproj(target, [], [], aspNet: false));
        foreach (var p in plans) write(P(domain, $"Entities/{p.ClassName}.cs"), DomainEntity(ns, p));

        // ---------------- Application
        write(P(application, application + ".csproj"), LibraryCsproj(target, [domain], [("Microsoft.Extensions.DependencyInjection.Abstractions", target.DiAbstractions)], aspNet: false));
        write(P(application, "Common/PagedResult.cs"), PagedResultCs(ns));
        write(P(application, "Common/Interfaces/IRepository.cs"), IRepositoryCs(ns));
        foreach (var p in plans)
        {
            var folder = $"Features/{p.SetName}";
            write(P(application, $"{folder}/{p.ClassName}Dto.cs"), DtoCs(ns, p, "Dto", DtoProps(p, "dto"), "The data returned for"));
            if (p.Operations.Contains("create")) write(P(application, $"{folder}/Create{p.ClassName}Request.cs"), DtoCs(ns, p, "Create", DtoProps(p, "create"), "What's needed to create"));
            if (p.Operations.Contains("update") && KeyOf(p) is not null) write(P(application, $"{folder}/Update{p.ClassName}Request.cs"), DtoCs(ns, p, "Update", DtoProps(p, "update"), "What can be changed on"));
            write(P(application, $"{folder}/{p.ClassName}Mappings.cs"), MappingsCs(ns, p));
            write(P(application, $"{folder}/I{p.ClassName}Service.cs"), ServiceInterfaceCs(ns, p));
            write(P(application, $"{folder}/{p.ClassName}Service.cs"), ServiceCs(ns, p));
        }
        if (jwt) write(P(application, "Auth/IAuthService.cs"), AuthContractsCs(ns));
        write(P(application, "DependencyInjection.cs"), ApplicationDiCs(ns, plans));

        // ---------------- Infrastructure
        var providerPackage = req.Connection.Provider switch
        {
            DbProvider.SqlServer => ("Microsoft.EntityFrameworkCore.SqlServer", target.EfSqlServer),
            DbProvider.MySql => ("Pomelo.EntityFrameworkCore.MySql", target.Pomelo),
            DbProvider.PostgreSql => ("Npgsql.EntityFrameworkCore.PostgreSQL", target.Npgsql),
            DbProvider.Oracle => ("Oracle.EntityFrameworkCore", target.OracleEf),
            _ => ("Microsoft.EntityFrameworkCore.Sqlite", target.EfSqlServer),
        };
        var infraPackages = new List<(string, string)> { providerPackage };
        if (jwt) infraPackages.Add(("Microsoft.AspNetCore.Authentication.JwtBearer", target.JwtBearer));
        write(P(infrastructure, infrastructure + ".csproj"), LibraryCsproj(target, [application, domain], infraPackages, aspNet: true));
        write(P(infrastructure, "Persistence/AppDbContext.cs"), CleanDbContextCs(ns, plans));
        foreach (var p in plans) write(P(infrastructure, $"Persistence/Configurations/{p.ClassName}Configuration.cs"), ConfigurationCs(ns, p, req.Connection.Provider));
        write(P(infrastructure, "Persistence/Repository.cs"), RepositoryCs(ns));
        if (jwt)
        {
            write(P(infrastructure, "Auth/AuthOptions.cs"), AuthOptionsCs(ns));
            write(P(infrastructure, "Auth/PasswordHasher.cs"), PasswordHasherCs(ns));
            write(P(infrastructure, "Auth/AuthService.cs"), AuthServiceCs(ns));
        }
        write(P(infrastructure, "DependencyInjection.cs"), InfrastructureDiCs(ns, req.Connection.Provider, mysqlVersion, jwt));

        // ---------------- Api
        write(P(apiName, apiName + ".csproj"), ApiCsproj(req, target, [application, infrastructure]));
        write(P(apiName, "Program.cs"), CleanProgramCs(req, ns, target, plans, jwt));
        if (target.BuiltInOpenApi && jwt) write(P(apiName, "OpenApi/BearerSecuritySchemeTransformer.cs"), BearerTransformerCs(ns, target));
        foreach (var p in plans)
        {
            if (minimal) write(P(apiName, $"Endpoints/{p.SetName}Endpoints.cs"), MinimalEndpointsCs(ns, p, target, jwt));
            else write(P(apiName, $"Controllers/{p.SetName}Controller.cs"), ControllerCs(ns, p, jwt));
        }
        if (jwt) write(P(apiName, minimal ? "Endpoints/AuthEndpoints.cs" : "Controllers/AuthController.cs"), minimal ? AuthEndpointsCs(ns, target) : AuthControllerCs(ns));
        var adminPassword = jwt ? NewPassword() : null;
        write(P(apiName, "appsettings.json"), CleanAppSettings(db.ConnectionString(req.Connection, forApp: true), name, jwt, adminPassword));
        write(P(apiName, "appsettings.Development.json"), Norm("""
            {
              "Logging": {
                "LogLevel": {
                  "Default": "Information",
                  "Microsoft.AspNetCore": "Warning",
                  "Microsoft.EntityFrameworkCore.Database.Command": "Information"
                }
              }
            }

            """));
        write(P(apiName, "Properties/launchSettings.json"), LaunchSettings(req.IncludeApiDocs, target));

        // ---------------- Solution
        var projects = new[] { domain, application, infrastructure, apiName };
        write(name + ".sln", SolutionFile(projects));
        write(".gitignore", Norm("""
            bin/
            obj/
            .vs/
            .vscode/
            .idea/
            *.user
            *.suo
            *.log
            appsettings.*.local.json

            """));
        write("README.md", CleanReadme(name, req, plans, target, jwt));
        return new CleanResult(Path.Combine(dir, name + ".sln"), Path.Combine(dir, P(apiName, apiName + ".csproj")), "admin", adminPassword);
    }

    // ------------------------------------------------------------------ project files

    private static string LibraryCsproj(TargetPackages target, string[] references, List<(string Id, string Version)> packages, bool aspNet)
    {
        var sb = new StringBuilder();
        sb.Append("<Project Sdk=\"Microsoft.NET.Sdk\">\n\n  <PropertyGroup>\n");
        sb.Append($"    <TargetFramework>{target.Tfm}</TargetFramework>\n    <Nullable>enable</Nullable>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n");
        if (aspNet) sb.Append("\n  <ItemGroup>\n    <FrameworkReference Include=\"Microsoft.AspNetCore.App\" />\n  </ItemGroup>\n");
        if (packages.Count > 0)
        {
            sb.Append("\n  <ItemGroup>\n");
            foreach (var (id, v) in packages) sb.Append($"    <PackageReference Include=\"{id}\" Version=\"{v}\" />\n");
            sb.Append("  </ItemGroup>\n");
        }
        if (references.Length > 0)
        {
            sb.Append("\n  <ItemGroup>\n");
            foreach (var r in references) sb.Append($"    <ProjectReference Include=\"..\\{r}\\{r}.csproj\" />\n");
            sb.Append("  </ItemGroup>\n");
        }
        sb.Append("\n</Project>\n");
        return Norm(sb.ToString());
    }

    private static string ApiCsproj(GenerateProjectRequest req, TargetPackages target, string[] references)
    {
        var packages = new List<(string, string)>();
        if (target.BuiltInOpenApi) packages.Add(("Microsoft.AspNetCore.OpenApi", target.OpenApi!));
        if (req.IncludeApiDocs && target.BuiltInOpenApi) packages.Add(("Scalar.AspNetCore", target.Scalar!));
        if (req.IncludeApiDocs && !target.BuiltInOpenApi) packages.Add(("Swashbuckle.AspNetCore", target.Swashbuckle!));
        var sb = new StringBuilder();
        sb.Append("<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n\n  <PropertyGroup>\n");
        sb.Append($"    <TargetFramework>{target.Tfm}</TargetFramework>\n    <Nullable>enable</Nullable>\n    <ImplicitUsings>enable</ImplicitUsings>\n");
        sb.Append("    <GenerateDocumentationFile>true</GenerateDocumentationFile>\n    <NoWarn>$(NoWarn);1591</NoWarn>\n  </PropertyGroup>\n");
        if (packages.Count > 0)
        {
            sb.Append("\n  <ItemGroup>\n");
            foreach (var (id, v) in packages) sb.Append($"    <PackageReference Include=\"{id}\" Version=\"{v}\" />\n");
            sb.Append("  </ItemGroup>\n");
        }
        sb.Append("\n  <ItemGroup>\n");
        foreach (var r in references) sb.Append($"    <ProjectReference Include=\"..\\{r}\\{r}.csproj\" />\n");
        sb.Append("  </ItemGroup>\n\n</Project>\n");
        return Norm(sb.ToString());
    }

    private static string SolutionFile(string[] projects)
    {
        static string G() => "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
        var src = G();
        var ids = projects.ToDictionary(p => p, _ => G());
        var sb = new StringBuilder();
        sb.Append("\nMicrosoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 17\nVisualStudioVersion = 17.0.31903.59\nMinimumVisualStudioVersion = 10.0.40219.1\n");
        sb.Append($"Project(\"{{2150E333-8FDC-42A3-9474-1A3956D46DE8}}\") = \"src\", \"src\", \"{src}\"\nEndProject\n");
        foreach (var p in projects) sb.Append($"Project(\"{{9A19103F-16F7-4668-BE54-9A1E7A4F7556}}\") = \"{p}\", \"src\\{p}\\{p}.csproj\", \"{ids[p]}\"\nEndProject\n");
        sb.Append("Global\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n\t\tDebug|Any CPU = Debug|Any CPU\n\t\tRelease|Any CPU = Release|Any CPU\n\tEndGlobalSection\n");
        sb.Append("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\n");
        foreach (var id in ids.Values)
            foreach (var c in new[] { "Debug", "Release" })
                sb.Append($"\t\t{id}.{c}|Any CPU.ActiveCfg = {c}|Any CPU\n\t\t{id}.{c}|Any CPU.Build.0 = {c}|Any CPU\n");
        sb.Append("\tEndGlobalSection\n\tGlobalSection(SolutionProperties) = preSolution\n\t\tHideSolutionNode = FALSE\n\tEndGlobalSection\n");
        sb.Append("\tGlobalSection(NestedProjects) = preSolution\n");
        foreach (var id in ids.Values) sb.Append($"\t\t{id} = {src}\n");
        sb.Append("\tEndGlobalSection\nEndGlobal\n");
        return Norm(sb.ToString());
    }

    // ------------------------------------------------------------------ shared shape helpers

    private static (DbColumn Column, string Property)? KeyOf(EntityPlan p)
    {
        var keys = p.Props.Where(x => x.Column.IsPrimaryKey).ToList();
        return keys.Count == 1 ? keys[0] : null;
    }

    private static (string Type, string Init) ClrOf(DbColumn col)
    {
        var isRef = col.ClrType is "string" or "byte[]";
        var nullable = col.Nullable || col.IsComputed && isRef;
        var type = col.ClrType + (nullable ? "?" : "");
        var init = isRef && !nullable ? (col.ClrType == "string" ? " = string.Empty;" : " = Array.Empty<byte>();") : "";
        return (type, init);
    }

    /// <summary>Which properties appear in the DTO, the create request and the update request.</summary>
    private static List<(DbColumn Column, string Property)> DtoProps(EntityPlan p, string kind) => kind switch
    {
        "create" => p.Props.Where(x => !x.Column.IsIdentity && !x.Column.IsComputed).ToList(),
        "update" => p.Props.Where(x => !x.Column.IsPrimaryKey && !x.Column.IsIdentity && !x.Column.IsComputed).ToList(),
        _ => p.Props,
    };

    private static string RouteConstraint(string clr) => clr switch { "int" => ":int", "long" => ":long", "Guid" => ":guid", "short" => ":int", _ => "" };
    private static string Route(EntityPlan p) => "/api/" + Kebab(p.SetName);
    private static string Lower(string s) => Words(s).ToLowerInvariant();

    // ------------------------------------------------------------------ Domain

    private static string DomainEntity(string ns, EntityPlan p)
    {
        var sb = new StringBuilder();
        sb.Append($"namespace {ns}.Domain.Entities;\n\n");
        sb.Append($"/// <summary>A {Lower(p.ClassName)} (stored in the {p.Table.Name} {(p.Table.IsView ? "view" : "table")}).</summary>\n");
        sb.Append($"public class {p.ClassName}\n{{\n");
        sb.Append(string.Join("\n", p.Props.Select(x => { var (t, init) = ClrOf(x.Column); return $"    public {t} {x.Property} {{ get; set; }}{init}"; })));
        sb.Append("\n}\n");
        return Norm(sb.ToString());
    }

    // ------------------------------------------------------------------ Application

    private static string PagedResultCs(string ns) => Norm($$"""
        namespace {{ns}}.Application.Common;

        /// <summary>One page of results plus what's needed to show paging.</summary>
        public sealed class PagedResult<T>
        {
            public PagedResult(IReadOnlyList<T> items, int page, int pageSize, int total)
            {
                Items = items;
                Page = page;
                PageSize = pageSize;
                Total = total;
            }

            public IReadOnlyList<T> Items { get; }
            public int Page { get; }
            public int PageSize { get; }
            public int Total { get; }
            public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
        }

        """);

    private static string IRepositoryCs(string ns) => Norm($$"""
        namespace {{ns}}.Application.Common.Interfaces;

        /// <summary>Stores and loads entities. The Infrastructure layer decides how (Entity Framework Core).</summary>
        public interface IRepository<T> where T : class
        {
            Task<IReadOnlyList<T>> ListAsync(int skip, int take, CancellationToken cancellationToken = default);
            Task<int> CountAsync(CancellationToken cancellationToken = default);
            Task<T?> FindAsync(object[] keyValues, CancellationToken cancellationToken = default);
            Task AddAsync(T entity, CancellationToken cancellationToken = default);
            void Remove(T entity);
            Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
        }

        """);

    private static string DtoCs(string ns, EntityPlan p, string kind, List<(DbColumn Column, string Property)> props, string summary)
    {
        var className = kind switch { "Dto" => p.ClassName + "Dto", "Create" => $"Create{p.ClassName}Request", _ => $"Update{p.ClassName}Request" };
        var validate = kind != "Dto";
        var sb = new StringBuilder();
        if (validate) sb.Append("using System.ComponentModel.DataAnnotations;\n\n");
        sb.Append($"namespace {ns}.Application.Features.{p.SetName};\n\n");
        sb.Append($"/// <summary>{summary} a {Lower(p.ClassName)}.</summary>\n");
        sb.Append($"public sealed class {className}\n{{\n");
        var lines = new List<string>();
        foreach (var (col, prop) in props)
        {
            var (t, init) = ClrOf(col);
            var attrs = new List<string>();
            if (validate && col.ClrType == "string" && !t.EndsWith('?')) attrs.Add("Required");
            if (validate && col.ClrType == "string" && col.MaxLength is > 0) attrs.Add($"MaxLength({col.MaxLength})");
            var attr = attrs.Count > 0 ? $"    [{string.Join(", ", attrs)}]\n" : "";
            lines.Add($"{attr}    public {t} {prop} {{ get; set; }}{init}");
        }
        sb.Append(string.Join(validate ? "\n\n" : "\n", lines));
        sb.Append("\n}\n");
        return Norm(sb.ToString());
    }

    private static string MappingsCs(string ns, EntityPlan p)
    {
        var sb = new StringBuilder();
        sb.Append($"using {ns}.Domain.Entities;\n\nnamespace {ns}.Application.Features.{p.SetName};\n\n");
        sb.Append($"/// <summary>Copies values between the {p.ClassName} entity and its DTOs.</summary>\n");
        sb.Append($"internal static class {p.ClassName}Mappings\n{{\n");
        sb.Append($"    public static {p.ClassName}Dto ToDto(this {p.ClassName} entity) => new()\n    {{\n");
        sb.Append(string.Join("\n", p.Props.Select(x => $"        {x.Property} = entity.{x.Property},"))).Append("\n    };\n");
        if (p.Operations.Contains("create"))
        {
            sb.Append($"\n    public static {p.ClassName} ToEntity(this Create{p.ClassName}Request request) => new()\n    {{\n");
            sb.Append(string.Join("\n", DtoProps(p, "create").Select(x => $"        {x.Property} = request.{x.Property},"))).Append("\n    };\n");
        }
        if (p.Operations.Contains("update") && KeyOf(p) is not null)
        {
            sb.Append($"\n    public static void ApplyTo(this Update{p.ClassName}Request request, {p.ClassName} entity)\n    {{\n");
            sb.Append(string.Join("\n", DtoProps(p, "update").Select(x => $"        entity.{x.Property} = request.{x.Property};"))).Append("\n    }\n");
        }
        sb.Append("}\n");
        return Norm(sb.ToString());
    }

    private static List<string> ServiceSignatures(EntityPlan p)
    {
        var key = KeyOf(p);
        var kt = key?.Column.ClrType ?? "int";
        var list = new List<string>();
        foreach (var op in new[] { "list", "get", "create", "update", "delete" }.Where(p.Operations.Contains))
        {
            if (op is "get" or "update" or "delete" && key is null) continue;
            list.Add(op switch
            {
                "list" => $"Task<PagedResult<{p.ClassName}Dto>> ListAsync(int page, int pageSize, CancellationToken cancellationToken = default)",
                "get" => $"Task<{p.ClassName}Dto?> GetAsync({kt} id, CancellationToken cancellationToken = default)",
                "create" => $"Task<{p.ClassName}Dto> CreateAsync(Create{p.ClassName}Request request, CancellationToken cancellationToken = default)",
                "update" => $"Task<bool> UpdateAsync({kt} id, Update{p.ClassName}Request request, CancellationToken cancellationToken = default)",
                _ => $"Task<bool> DeleteAsync({kt} id, CancellationToken cancellationToken = default)",
            });
        }
        return list;
    }

    private static string ServiceInterfaceCs(string ns, EntityPlan p)
    {
        var sb = new StringBuilder();
        sb.Append($"using {ns}.Application.Common;\n\nnamespace {ns}.Application.Features.{p.SetName};\n\n");
        sb.Append($"/// <summary>Everything the API can do with {Lower(p.SetName)}.</summary>\n");
        sb.Append($"public interface I{p.ClassName}Service\n{{\n");
        sb.Append(string.Join("\n", ServiceSignatures(p).Select(s => $"    {s};")));
        sb.Append("\n}\n");
        return Norm(sb.ToString());
    }

    private static string ServiceCs(string ns, EntityPlan p)
    {
        var key = KeyOf(p);
        var kt = key?.Column.ClrType ?? "int";
        var body = new List<string>();
        foreach (var op in new[] { "list", "get", "create", "update", "delete" }.Where(p.Operations.Contains))
        {
            if (op is "get" or "update" or "delete" && key is null) continue;
            body.Add(op switch
            {
                "list" => $$"""
                        public async Task<PagedResult<{{p.ClassName}}Dto>> ListAsync(int page, int pageSize, CancellationToken cancellationToken = default)
                        {
                            page = Math.Max(1, page);
                            pageSize = Math.Clamp(pageSize, 1, 100);
                            var total = await _repository.CountAsync(cancellationToken);
                            var items = await _repository.ListAsync((page - 1) * pageSize, pageSize, cancellationToken);
                            return new PagedResult<{{p.ClassName}}Dto>(items.Select(e => e.ToDto()).ToList(), page, pageSize, total);
                        }
                    """,
                "get" => $$"""
                        public async Task<{{p.ClassName}}Dto?> GetAsync({{kt}} id, CancellationToken cancellationToken = default)
                        {
                            var entity = await _repository.FindAsync(new object[] { id }, cancellationToken);
                            return entity?.ToDto();
                        }
                    """,
                "create" => $$"""
                        public async Task<{{p.ClassName}}Dto> CreateAsync(Create{{p.ClassName}}Request request, CancellationToken cancellationToken = default)
                        {
                            var entity = request.ToEntity();
                            await _repository.AddAsync(entity, cancellationToken);
                            await _repository.SaveChangesAsync(cancellationToken);
                            return entity.ToDto();
                        }
                    """,
                "update" => $$"""
                        public async Task<bool> UpdateAsync({{kt}} id, Update{{p.ClassName}}Request request, CancellationToken cancellationToken = default)
                        {
                            var entity = await _repository.FindAsync(new object[] { id }, cancellationToken);
                            if (entity is null) return false;
                            request.ApplyTo(entity);
                            await _repository.SaveChangesAsync(cancellationToken);
                            return true;
                        }
                    """,
                _ => $$"""
                        public async Task<bool> DeleteAsync({{kt}} id, CancellationToken cancellationToken = default)
                        {
                            var entity = await _repository.FindAsync(new object[] { id }, cancellationToken);
                            if (entity is null) return false;
                            _repository.Remove(entity);
                            await _repository.SaveChangesAsync(cancellationToken);
                            return true;
                        }
                    """,
            });
        }
        var sb = new StringBuilder();
        sb.Append($"using {ns}.Application.Common;\nusing {ns}.Application.Common.Interfaces;\nusing {ns}.Domain.Entities;\n\n");
        sb.Append($"namespace {ns}.Application.Features.{p.SetName};\n\n");
        sb.Append($"public class {p.ClassName}Service : I{p.ClassName}Service\n{{\n");
        sb.Append($"    private readonly IRepository<{p.ClassName}> _repository;\n\n");
        sb.Append($"    public {p.ClassName}Service(IRepository<{p.ClassName}> repository)\n    {{\n        _repository = repository;\n    }}\n");
        foreach (var b in body) sb.Append('\n').Append(b.TrimEnd()).Append('\n');
        sb.Append("}\n");
        return Norm(sb.ToString());
    }

    private static string ApplicationDiCs(string ns, List<EntityPlan> plans)
    {
        var sb = new StringBuilder();
        sb.Append("using Microsoft.Extensions.DependencyInjection;\n");
        foreach (var p in plans) sb.Append($"using {ns}.Application.Features.{p.SetName};\n");
        sb.Append($"\nnamespace {ns}.Application;\n\n");
        sb.Append("public static class DependencyInjection\n{\n");
        sb.Append("    /// <summary>Registers the application's use cases.</summary>\n");
        sb.Append("    public static IServiceCollection AddApplication(this IServiceCollection services)\n    {\n");
        foreach (var p in plans) sb.Append($"        services.AddScoped<I{p.ClassName}Service, {p.ClassName}Service>();\n");
        sb.Append("        return services;\n    }\n}\n");
        return Norm(sb.ToString());
    }

    private static string AuthContractsCs(string ns) => Norm($$"""
        using System.ComponentModel.DataAnnotations;

        namespace {{ns}}.Application.Auth;

        /// <summary>Signs users in and issues access tokens.</summary>
        public interface IAuthService
        {
            /// <summary>Returns a token, or null when the user name or password is wrong.</summary>
            Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
        }

        public sealed class LoginRequest
        {
            [Required]
            public string UserName { get; set; } = string.Empty;

            [Required]
            public string Password { get; set; } = string.Empty;
        }

        public sealed class LoginResponse
        {
            public string AccessToken { get; set; } = string.Empty;
            public string TokenType { get; set; } = "Bearer";
            public DateTime ExpiresAt { get; set; }
            public string UserName { get; set; } = string.Empty;
            public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
        }

        """);

    // ------------------------------------------------------------------ Infrastructure

    private static string CleanDbContextCs(string ns, List<EntityPlan> plans)
    {
        var sb = new StringBuilder();
        sb.Append($"using Microsoft.EntityFrameworkCore;\nusing {ns}.Domain.Entities;\n\nnamespace {ns}.Infrastructure.Persistence;\n\n");
        sb.Append("public class AppDbContext : DbContext\n{\n");
        sb.Append("    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)\n    {\n    }\n\n");
        foreach (var p in plans) sb.Append($"    public DbSet<{p.ClassName}> {p.SetName} => Set<{p.ClassName}>();\n");
        sb.Append("\n    protected override void OnModelCreating(ModelBuilder modelBuilder)\n    {\n");
        sb.Append("        // Table and column mappings live in Persistence/Configurations\n");
        sb.Append("        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);\n    }\n}\n");
        return Norm(sb.ToString());
    }

    private static string ConfigurationCs(string ns, EntityPlan p, DbProvider provider)
    {
        var schemaArg = provider is DbProvider.SqlServer or DbProvider.PostgreSql or DbProvider.Oracle && p.Table.Schema.Length > 0 ? $", {Lit(p.Table.Schema)}" : "";
        var keys = p.Props.Where(x => x.Column.IsPrimaryKey).Select(x => x.Property).ToList();
        var sb = new StringBuilder();
        sb.Append($"using Microsoft.EntityFrameworkCore;\nusing Microsoft.EntityFrameworkCore.Metadata.Builders;\nusing {ns}.Domain.Entities;\n\n");
        sb.Append($"namespace {ns}.Infrastructure.Persistence.Configurations;\n\n");
        sb.Append($"public class {p.ClassName}Configuration : IEntityTypeConfiguration<{p.ClassName}>\n{{\n");
        sb.Append($"    public void Configure(EntityTypeBuilder<{p.ClassName}> builder)\n    {{\n");
        sb.Append(p.Table.IsView ? $"        builder.ToView({Lit(p.Table.Name)}{schemaArg});\n" : $"        builder.ToTable({Lit(p.Table.Name)}{schemaArg});\n");
        sb.Append(keys.Count switch
        {
            0 => "        builder.HasNoKey();\n",
            1 => $"        builder.HasKey(e => e.{keys[0]});\n",
            _ => $"        builder.HasKey(e => new {{ {string.Join(", ", keys.Select(k => "e." + k))} }});\n",
        });
        foreach (var (col, prop) in p.Props)
        {
            var chain = new List<string>();
            if (prop != col.Name) chain.Add($".HasColumnName({Lit(col.Name)})");
            if (col.ClrType == "string" && col.MaxLength is > 0) chain.Add($".HasMaxLength({col.MaxLength})");
            if (provider == DbProvider.PostgreSql && col.SqlType is "json" or "jsonb" or "xml" or "citext" or "inet" or "cidr") chain.Add($".HasColumnType({Lit(col.SqlType)})");
            if (col.IsPrimaryKey && keys.Count == 1)
            {
                if (col.IsIdentity) chain.Add(".ValueGeneratedOnAdd()");
                else if (col.ClrType is "int" or "long" or "short" or "byte") chain.Add(".ValueGeneratedNever()");
            }
            if (col.IsComputed) chain.Add(col.SqlType is "timestamp" or "rowversion" ? ".IsRowVersion()" : ".ValueGeneratedOnAddOrUpdate()");
            if (chain.Count > 0) sb.Append($"        builder.Property(e => e.{prop}){string.Concat(chain)};\n");
        }
        sb.Append("    }\n}\n");
        return Norm(sb.ToString());
    }

    private static string RepositoryCs(string ns) => Norm($$"""
        using Microsoft.EntityFrameworkCore;
        using {{ns}}.Application.Common.Interfaces;

        namespace {{ns}}.Infrastructure.Persistence;

        /// <summary>Entity Framework Core implementation of <see cref="IRepository{T}"/>.</summary>
        public class Repository<T> : IRepository<T> where T : class
        {
            private readonly AppDbContext _db;

            public Repository(AppDbContext db)
            {
                _db = db;
            }

            public async Task<IReadOnlyList<T>> ListAsync(int skip, int take, CancellationToken cancellationToken = default) =>
                await OrderedByKey(_db.Set<T>().AsNoTracking()).Skip(skip).Take(take).ToListAsync(cancellationToken);

            public Task<int> CountAsync(CancellationToken cancellationToken = default) => _db.Set<T>().CountAsync(cancellationToken);

            public async Task<T?> FindAsync(object[] keyValues, CancellationToken cancellationToken = default) =>
                await _db.Set<T>().FindAsync(keyValues, cancellationToken);

            public async Task AddAsync(T entity, CancellationToken cancellationToken = default) => await _db.Set<T>().AddAsync(entity, cancellationToken);

            public void Remove(T entity) => _db.Set<T>().Remove(entity);

            public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => _db.SaveChangesAsync(cancellationToken);

            /// <summary>Paging needs a stable order, so rows are sorted by their key.</summary>
            private IQueryable<T> OrderedByKey(IQueryable<T> query)
            {
                var key = _db.Model.FindEntityType(typeof(T))?.FindPrimaryKey();
                if (key is null) return query;
                IOrderedQueryable<T>? ordered = null;
                foreach (var property in key.Properties)
                {
                    var name = property.Name;
                    ordered = ordered is null ? query.OrderBy(e => EF.Property<object>(e, name)) : ordered.ThenBy(e => EF.Property<object>(e, name));
                }
                return ordered ?? query;
            }
        }

        """);

    private static string AuthOptionsCs(string ns) => Norm($$"""
        namespace {{ns}}.Infrastructure.Auth;

        /// <summary>Settings for the access tokens (section "Jwt" in appsettings.json).</summary>
        public sealed class JwtOptions
        {
            public const string SectionName = "Jwt";

            public string Issuer { get; set; } = string.Empty;
            public string Audience { get; set; } = string.Empty;

            /// <summary>Secret used to sign tokens. Keep it out of source control in production (environment variable Jwt__Key).</summary>
            public string Key { get; set; } = string.Empty;

            public int ExpiresMinutes { get; set; } = 60;
        }

        /// <summary>Users who may sign in (section "Auth" in appsettings.json). Replace with a user table when you need one.</summary>
        public sealed class AuthOptions
        {
            public const string SectionName = "Auth";

            public List<AuthUser> Users { get; set; } = new();
        }

        public sealed class AuthUser
        {
            public string UserName { get; set; } = string.Empty;

            /// <summary>Created with <see cref="PasswordHasher.Hash"/>; passwords are never stored as plain text.</summary>
            public string PasswordHash { get; set; } = string.Empty;

            public List<string> Roles { get; set; } = new();
        }

        """);

    private static string PasswordHasherCs(string ns) => Norm($$"""
        using System.Security.Cryptography;

        namespace {{ns}}.Infrastructure.Auth;

        /// <summary>PBKDF2 password hashing with a random salt per password.</summary>
        public static class PasswordHasher
        {
            private const int Iterations = 100_000;

            public static string Hash(string password)
            {
                var salt = RandomNumberGenerator.GetBytes(16);
                var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
                return $"PBKDF2-SHA256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
            }

            public static bool Verify(string password, string stored)
            {
                var parts = stored.Split('$');
                if (parts.Length != 4 || parts[0] != "PBKDF2-SHA256" || !int.TryParse(parts[1], out var iterations)) return false;
                var salt = Convert.FromBase64String(parts[2]);
                var expected = Convert.FromBase64String(parts[3]);
                var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
        }

        """);

    private static string AuthServiceCs(string ns) => Norm($$"""
        using System.IdentityModel.Tokens.Jwt;
        using System.Security.Claims;
        using System.Text;
        using Microsoft.Extensions.Options;
        using Microsoft.IdentityModel.Tokens;
        using {{ns}}.Application.Auth;

        namespace {{ns}}.Infrastructure.Auth;

        public class AuthService : IAuthService
        {
            // Checked when the user doesn't exist, so a wrong user name takes as long as a wrong password
            private static readonly string DummyHash = PasswordHasher.Hash(Guid.NewGuid().ToString());

            private readonly JwtOptions _jwt;
            private readonly AuthOptions _auth;

            public AuthService(IOptions<JwtOptions> jwt, IOptions<AuthOptions> auth)
            {
                _jwt = jwt.Value;
                _auth = auth.Value;
            }

            public Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
            {
                var user = _auth.Users.FirstOrDefault(u => string.Equals(u.UserName, request.UserName, StringComparison.OrdinalIgnoreCase));
                var valid = PasswordHasher.Verify(request.Password, user?.PasswordHash ?? DummyHash) && user is not null;
                return Task.FromResult(valid ? CreateToken(user!) : null);
            }

            private LoginResponse CreateToken(AuthUser user)
            {
                var claims = new List<Claim>
                {
                    new(JwtRegisteredClaimNames.Sub, user.UserName),
                    new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                    new(ClaimTypes.Name, user.UserName),
                };
                claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

                var expires = DateTime.UtcNow.AddMinutes(_jwt.ExpiresMinutes);
                var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Key)), SecurityAlgorithms.HmacSha256);
                var token = new JwtSecurityToken(_jwt.Issuer, _jwt.Audience, claims, expires: expires, signingCredentials: credentials);

                return new LoginResponse
                {
                    AccessToken = new JwtSecurityTokenHandler().WriteToken(token),
                    ExpiresAt = expires,
                    UserName = user.UserName,
                    Roles = user.Roles,
                };
            }
        }

        """);

    private static string InfrastructureDiCs(string ns, DbProvider provider, string? mysqlVersion, bool jwt)
    {
        var use = provider switch
        {
            DbProvider.SqlServer => "options.UseSqlServer(connectionString)",
            DbProvider.PostgreSql => "options.UseNpgsql(connectionString)",
            DbProvider.Oracle => "options.UseOracle(connectionString)",
            DbProvider.Sqlite => "options.UseSqlite(connectionString)",
            _ => MySqlUse(mysqlVersion),
        };
        var sb = new StringBuilder();
        if (jwt) sb.Append("using System.Security.Claims;\nusing System.Text;\nusing Microsoft.AspNetCore.Authentication.JwtBearer;\n");
        sb.Append("using Microsoft.EntityFrameworkCore;\nusing Microsoft.Extensions.Configuration;\nusing Microsoft.Extensions.DependencyInjection;\n");
        if (jwt) sb.Append("using Microsoft.IdentityModel.Tokens;\n");
        if (jwt) sb.Append($"using {ns}.Application.Auth;\n");
        sb.Append($"using {ns}.Application.Common.Interfaces;\n");
        if (jwt) sb.Append($"using {ns}.Infrastructure.Auth;\n");
        sb.Append($"using {ns}.Infrastructure.Persistence;\n\n");
        sb.Append($"namespace {ns}.Infrastructure;\n\npublic static class DependencyInjection\n{{\n");
        sb.Append("    /// <summary>Registers the database and other outside-world services.</summary>\n");
        sb.Append("    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)\n    {\n");
        sb.Append("        var connectionString = configuration.GetConnectionString(\"Default\")\n");
        sb.Append("            ?? throw new InvalidOperationException(\"The connection string 'Default' is missing from appsettings.json.\");\n");
        if (provider == DbProvider.PostgreSql)
            sb.Append("        // Accept dates without a time zone, as most clients send them\n        AppContext.SetSwitch(\"Npgsql.EnableLegacyTimestampBehavior\", true);\n");
        sb.Append($"        services.AddDbContext<AppDbContext>(options => {use});\n");
        sb.Append("        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));\n");
        if (jwt)
        {
            sb.Append(Norm("""

                        // Sign-in with JSON Web Tokens
                        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
                        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
                        var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
                        if (jwt.Key.Length < 32)
                            throw new InvalidOperationException("Jwt:Key must be a secret of at least 32 characters (set it in appsettings.json or the Jwt__Key environment variable).");

                        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                            .AddJwtBearer(options =>
                            {
                                options.TokenValidationParameters = new TokenValidationParameters
                                {
                                    ValidateIssuer = true,
                                    ValidIssuer = jwt.Issuer,
                                    ValidateAudience = true,
                                    ValidAudience = jwt.Audience,
                                    ValidateIssuerSigningKey = true,
                                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
                                    ValidateLifetime = true,
                                    ClockSkew = TimeSpan.FromMinutes(1),
                                    NameClaimType = ClaimTypes.Name,
                                    RoleClaimType = ClaimTypes.Role,
                                };
                            });
                        services.AddAuthorization();
                        services.AddScoped<IAuthService, AuthService>();

                """).Replace(Nl, "\n"));
        }
        sb.Append("        return services;\n    }\n}\n");
        return Norm(sb.ToString());
    }

    private static string MySqlUse(string? mysqlVersion)
    {
        var v = System.Text.RegularExpressions.Regex.Match(mysqlVersion ?? "8.0.0", @"\d+\.\d+\.\d+").Value;
        if (v.Length == 0) v = "8.0.0";
        var kind = (mysqlVersion ?? "").Contains("mariadb", StringComparison.OrdinalIgnoreCase) ? "MariaDbServerVersion" : "MySqlServerVersion";
        return $"options.UseMySql(connectionString, new {kind}(new Version({v.Replace(".", ", ")})))";
    }

    // ------------------------------------------------------------------ Api

    private static string CleanProgramCs(GenerateProjectRequest req, string ns, TargetPackages target, List<EntityPlan> plans, bool jwt)
    {
        var minimal = req.Style == EndpointStyle.MinimalApi;
        var docs = req.IncludeApiDocs;
        var l = new List<string>();
        if (docs && !target.BuiltInOpenApi && jwt) l.Add("using Microsoft.OpenApi.Models;");
        if (docs && target.BuiltInOpenApi) l.Add("using Scalar.AspNetCore;");
        if (minimal) l.Add($"using {ns}.Api.Endpoints;");
        if (target.BuiltInOpenApi && jwt) l.Add($"using {ns}.Api.OpenApi;");
        l.Add($"using {ns}.Application;");
        l.Add($"using {ns}.Infrastructure;");
        l.Add("");
        l.Add("var builder = WebApplication.CreateBuilder(args);");
        l.Add("");
        l.Add("// Layers: Application (use cases) and Infrastructure (database" + (jwt ? ", sign-in" : "") + ")");
        l.Add("builder.Services.AddApplication();");
        l.Add("builder.Services.AddInfrastructure(builder.Configuration);");
        l.Add("");
        l.Add("// HTTP");
        if (!minimal) l.Add("builder.Services.AddControllers();");
        if (target.BuiltInOpenApi)
            l.Add(jwt ? "builder.Services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());" : "builder.Services.AddOpenApi();");
        else if (docs)
        {
            l.Add("builder.Services.AddEndpointsApiExplorer();");
            if (!jwt) l.Add("builder.Services.AddSwaggerGen();");
            else
            {
                l.Add("builder.Services.AddSwaggerGen(options =>");
                l.Add("{");
                l.Add("    options.AddSecurityDefinition(\"Bearer\", new OpenApiSecurityScheme");
                l.Add("    {");
                l.Add("        Type = SecuritySchemeType.Http,");
                l.Add("        Scheme = \"bearer\",");
                l.Add("        BearerFormat = \"JWT\",");
                l.Add("        Description = \"Sign in with POST /api/auth/login and paste the accessToken here.\",");
                l.Add("    });");
                l.Add("    options.AddSecurityRequirement(new OpenApiSecurityRequirement");
                l.Add("    {");
                l.Add("        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = \"Bearer\" } }] = Array.Empty<string>(),");
                l.Add("    });");
                l.Add("});");
            }
        }
        l.Add("builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));");
        if (target.Major >= 8) l.Add("builder.Services.AddProblemDetails();");
        l.Add("");
        l.Add("var app = builder.Build();");
        l.Add("");
        if (target.Major >= 8) l.Add("app.UseExceptionHandler();");
        l.Add("app.UseCors();");
        if (jwt) { l.Add("app.UseAuthentication();"); l.Add("app.UseAuthorization();"); }
        l.Add("");
        if (target.BuiltInOpenApi) l.Add("app.MapOpenApi();");
        if (docs && target.BuiltInOpenApi)
        {
            l.Add("app.MapScalarApiReference();");
            l.Add("app.MapGet(\"/\", () => Results.Redirect(\"/scalar\")).ExcludeFromDescription();");
        }
        else if (docs)
        {
            l.Add("app.UseSwagger();");
            l.Add("app.UseSwaggerUI();");
            l.Add("app.MapGet(\"/\", () => Results.Redirect(\"/swagger\")).ExcludeFromDescription();");
        }
        l.Add("app.MapGet(\"/health\", () => Results.Ok(new { status = \"Healthy\" })).ExcludeFromDescription();");
        l.Add("");
        if (minimal)
        {
            if (jwt) l.Add("app.MapAuthEndpoints();");
            foreach (var p in plans) l.Add($"app.Map{p.SetName}Endpoints();");
        }
        else l.Add("app.MapControllers();");
        l.Add("");
        l.Add("app.Run();");
        return Norm(string.Join("\n", l) + "\n");
    }

    private static string BearerTransformerCs(string ns, TargetPackages target) => target.Major >= 10
        ? Norm($$"""
            using Microsoft.AspNetCore.OpenApi;
            using Microsoft.OpenApi;

            namespace {{ns}}.Api.OpenApi;

            /// <summary>Tells the API docs that endpoints take a bearer token, so they can send one.</summary>
            internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
            {
                public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
                {
                    document.Components ??= new OpenApiComponents();
                    document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                    document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT",
                        Description = "Sign in with POST /api/auth/login and paste the accessToken here.",
                    };
                    document.Security ??= new List<OpenApiSecurityRequirement>();
                    document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = new List<string>() });
                    return Task.CompletedTask;
                }
            }

            """)
        : Norm($$"""
            using Microsoft.AspNetCore.OpenApi;
            using Microsoft.OpenApi.Models;

            namespace {{ns}}.Api.OpenApi;

            /// <summary>Tells the API docs that endpoints take a bearer token, so they can send one.</summary>
            internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
            {
                public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
                {
                    document.Components ??= new OpenApiComponents();
                    document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT",
                        Description = "Sign in with POST /api/auth/login and paste the accessToken here.",
                    };
                    document.SecurityRequirements.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>(),
                    });
                    return Task.CompletedTask;
                }
            }

            """);

    private static string ControllerCs(string ns, EntityPlan p, bool jwt)
    {
        var key = KeyOf(p);
        var kt = key?.Column.ClrType ?? "int";
        var idRoute = "{id" + RouteConstraint(kt) + "}";
        var hasGet = p.Operations.Contains("get") && key is not null;
        var methods = new List<string>();
        foreach (var op in new[] { "list", "get", "create", "update", "delete" }.Where(p.Operations.Contains))
        {
            if (op is "get" or "update" or "delete" && key is null) continue;
            methods.Add(op switch
            {
                "list" => $$"""
                        /// <summary>Get a page of {{Lower(p.SetName)}}</summary>
                        [HttpGet]
                        public async Task<ActionResult<PagedResult<{{p.ClassName}}Dto>>> Get{{p.SetName}}([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
                        {
                            return Ok(await _service.ListAsync(page, pageSize, cancellationToken));
                        }
                    """,
                "get" => $$"""
                        /// <summary>Get one {{Lower(p.ClassName)}} by its ID</summary>
                        [HttpGet("{{idRoute}}")]
                        public async Task<ActionResult<{{p.ClassName}}Dto>> Get{{p.ClassName}}ById({{kt}} id, CancellationToken cancellationToken)
                        {
                            var item = await _service.GetAsync(id, cancellationToken);
                            return item is null ? NotFound() : Ok(item);
                        }
                    """,
                "create" => key is not null && hasGet
                    ? $$"""
                        /// <summary>Create a new {{Lower(p.ClassName)}}</summary>
                        [HttpPost]
                        public async Task<ActionResult<{{p.ClassName}}Dto>> Create{{p.ClassName}}(Create{{p.ClassName}}Request request, CancellationToken cancellationToken)
                        {
                            var created = await _service.CreateAsync(request, cancellationToken);
                            return CreatedAtAction(nameof(Get{{p.ClassName}}ById), new { id = created.{{key.Value.Property}} }, created);
                        }
                    """
                    : $$"""
                        /// <summary>Create a new {{Lower(p.ClassName)}}</summary>
                        [HttpPost]
                        public async Task<ActionResult<{{p.ClassName}}Dto>> Create{{p.ClassName}}(Create{{p.ClassName}}Request request, CancellationToken cancellationToken)
                        {
                            var created = await _service.CreateAsync(request, cancellationToken);
                            return StatusCode(StatusCodes.Status201Created, created);
                        }
                    """,
                "update" => $$"""
                        /// <summary>Update an existing {{Lower(p.ClassName)}}</summary>
                        [HttpPut("{{idRoute}}")]
                        public async Task<IActionResult> Update{{p.ClassName}}({{kt}} id, Update{{p.ClassName}}Request request, CancellationToken cancellationToken)
                        {
                            return await _service.UpdateAsync(id, request, cancellationToken) ? NoContent() : NotFound();
                        }
                    """,
                _ => $$"""
                        /// <summary>Delete a {{Lower(p.ClassName)}}</summary>
                        [HttpDelete("{{idRoute}}")]
                        public async Task<IActionResult> Delete{{p.ClassName}}({{kt}} id, CancellationToken cancellationToken)
                        {
                            return await _service.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
                        }
                    """,
            });
        }
        var sb = new StringBuilder();
        if (jwt) sb.Append("using Microsoft.AspNetCore.Authorization;\n");
        sb.Append($"using Microsoft.AspNetCore.Mvc;\nusing {ns}.Application.Common;\nusing {ns}.Application.Features.{p.SetName};\n\n");
        sb.Append($"namespace {ns}.Api.Controllers;\n\n");
        sb.Append("[ApiController]\n");
        sb.Append($"[Route(\"{Route(p).TrimStart('/')}\")]\n");
        if (jwt) sb.Append("[Authorize]\n");
        sb.Append($"public class {p.SetName}Controller : ControllerBase\n{{\n");
        sb.Append($"    private readonly I{p.ClassName}Service _service;\n\n");
        sb.Append($"    public {p.SetName}Controller(I{p.ClassName}Service service)\n    {{\n        _service = service;\n    }}\n");
        foreach (var m in methods) sb.Append('\n').Append(m.TrimEnd()).Append('\n');
        sb.Append("}\n");
        return Norm(sb.ToString());
    }

    private static string MinimalEndpointsCs(string ns, EntityPlan p, TargetPackages target, bool jwt)
    {
        var key = KeyOf(p);
        var kt = key?.Column.ClrType ?? "int";
        var idRoute = "{id" + RouteConstraint(kt) + "}";
        var grouped = target.Major >= 7;
        var route = Route(p);
        var summary = target.Major >= 7;
        string R(string rel) => grouped ? (rel.Length == 0 ? "/" : "/" + rel) : route + (rel.Length == 0 ? "" : "/" + rel);
        string Tail(string name, string text) => $".WithName(\"{name}\")" + (summary ? $".WithSummary(\"{text}\")" : "") + (!grouped ? $".WithTags(\"{p.SetName}\")" + (jwt ? ".RequireAuthorization()" : "") : "") + ";";
        var target0 = grouped ? "group" : "app";
        var lines = new List<string>();
        foreach (var op in new[] { "list", "get", "create", "update", "delete" }.Where(p.Operations.Contains))
        {
            if (op is "get" or "update" or "delete" && key is null) continue;
            lines.Add(op switch
            {
                "list" => $"        {target0}.MapGet(\"{R("")}\", async (I{p.ClassName}Service service, int? page, int? pageSize, CancellationToken cancellationToken) =>\n" +
                          $"            Results.Ok(await service.ListAsync(page ?? 1, pageSize ?? 20, cancellationToken))){Tail($"Get{p.SetName}", $"Get a page of {Lower(p.SetName)}")}",
                "get" => $"        {target0}.MapGet(\"{R(idRoute)}\", async ({kt} id, I{p.ClassName}Service service, CancellationToken cancellationToken) =>\n" +
                         $"            await service.GetAsync(id, cancellationToken) is {{ }} item ? Results.Ok(item) : Results.NotFound()){Tail($"Get{p.ClassName}ById", $"Get one {Lower(p.ClassName)} by its ID")}",
                "create" => $"        {target0}.MapPost(\"{R("")}\", async (Create{p.ClassName}Request request, I{p.ClassName}Service service, CancellationToken cancellationToken) =>\n" +
                            "        {\n" +
                            "            var created = await service.CreateAsync(request, cancellationToken);\n" +
                            (key is not null ? $"            return Results.Created($\"{route}/{{created.{key.Value.Property}}}\", created);\n" : $"            return Results.Created(\"{route}\", created);\n") +
                            $"        }}){Tail($"Create{p.ClassName}", $"Create a new {Lower(p.ClassName)}")}",
                "update" => $"        {target0}.MapPut(\"{R(idRoute)}\", async ({kt} id, Update{p.ClassName}Request request, I{p.ClassName}Service service, CancellationToken cancellationToken) =>\n" +
                            $"            await service.UpdateAsync(id, request, cancellationToken) ? Results.NoContent() : Results.NotFound()){Tail($"Update{p.ClassName}", $"Update an existing {Lower(p.ClassName)}")}",
                _ => $"        {target0}.MapDelete(\"{R(idRoute)}\", async ({kt} id, I{p.ClassName}Service service, CancellationToken cancellationToken) =>\n" +
                     $"            await service.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound()){Tail($"Delete{p.ClassName}", $"Delete a {Lower(p.ClassName)}")}",
            });
        }
        var sb = new StringBuilder();
        sb.Append($"using {ns}.Application.Features.{p.SetName};\n\nnamespace {ns}.Api.Endpoints;\n\n");
        sb.Append($"public static class {p.SetName}Endpoints\n{{\n");
        sb.Append($"    public static IEndpointRouteBuilder Map{p.SetName}Endpoints(this IEndpointRouteBuilder app)\n    {{\n");
        if (grouped) sb.Append($"        var group = app.MapGroup(\"{route}\").WithTags(\"{p.SetName}\"){(jwt ? ".RequireAuthorization()" : "")};\n\n");
        sb.Append(string.Join("\n\n", lines)).Append("\n\n        return app;\n    }\n}\n");
        return Norm(sb.ToString());
    }

    private static string AuthControllerCs(string ns) => Norm($$"""
        using System.Security.Claims;
        using Microsoft.AspNetCore.Authorization;
        using Microsoft.AspNetCore.Mvc;
        using {{ns}}.Application.Auth;

        namespace {{ns}}.Api.Controllers;

        [ApiController]
        [Route("api/auth")]
        public class AuthController : ControllerBase
        {
            private readonly IAuthService _auth;

            public AuthController(IAuthService auth)
            {
                _auth = auth;
            }

            /// <summary>Sign in and get an access token</summary>
            [HttpPost("login")]
            [AllowAnonymous]
            public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
            {
                var result = await _auth.LoginAsync(request, cancellationToken);
                return result is null ? Unauthorized() : Ok(result);
            }

            /// <summary>Who is signed in</summary>
            [HttpGet("me")]
            [Authorize]
            public IActionResult Me()
            {
                return Ok(new { userName = User.Identity?.Name, roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value) });
            }
        }

        """);

    private static string AuthEndpointsCs(string ns, TargetPackages target)
    {
        var summary = target.Major >= 7;
        string S(string t) => summary ? $".WithSummary(\"{t}\")" : "";
        return Norm($$"""
            using System.Security.Claims;
            using {{ns}}.Application.Auth;

            namespace {{ns}}.Api.Endpoints;

            public static class AuthEndpoints
            {
                public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
                {
                    app.MapPost("/api/auth/login", async (LoginRequest request, IAuthService auth, CancellationToken cancellationToken) =>
                        await auth.LoginAsync(request, cancellationToken) is { } result ? Results.Ok(result) : Results.Unauthorized())
                        .WithName("Login").WithTags("Auth"){{S("Sign in and get an access token")}}.AllowAnonymous();

                    app.MapGet("/api/auth/me", (ClaimsPrincipal user) =>
                        Results.Ok(new { userName = user.Identity?.Name, roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value) }))
                        .WithName("Me").WithTags("Auth"){{S("Who is signed in")}}.RequireAuthorization();

                    return app;
                }
            }

            """);
    }

    // ------------------------------------------------------------------ settings & docs

    private static string NewPassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(14);
        return string.Concat(bytes.Select(b => chars[b % chars.Length])) + "!7";
    }

    /// <summary>Same PBKDF2 format the generated PasswordHasher reads.</summary>
    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"PBKDF2-SHA256$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static string CleanAppSettings(string connectionString, string name, bool jwt, string? adminPassword)
    {
        var root = new JsonObject
        {
            ["ConnectionStrings"] = new JsonObject { ["Default"] = connectionString },
        };
        if (jwt)
        {
            root["Jwt"] = new JsonObject
            {
                ["Issuer"] = name,
                ["Audience"] = name,
                ["Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
                ["ExpiresMinutes"] = 60,
            };
            root["Auth"] = new JsonObject
            {
                ["Users"] = new JsonArray(new JsonObject
                {
                    ["UserName"] = "admin",
                    ["PasswordHash"] = HashPassword(adminPassword!),
                    ["Roles"] = new JsonArray("Admin"),
                }),
            };
        }
        root["Logging"] = new JsonObject { ["LogLevel"] = new JsonObject { ["Default"] = "Information", ["Microsoft.AspNetCore"] = "Warning" } };
        root["AllowedHosts"] = "*";
        return Norm(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static string CleanReadme(string name, GenerateProjectRequest req, List<EntityPlan> plans, TargetPackages target, bool jwt)
    {
        var providerName = req.Connection.Provider switch
        {
            DbProvider.SqlServer => "SQL Server", DbProvider.MySql => "MySQL", DbProvider.PostgreSql => "PostgreSQL", DbProvider.Oracle => "Oracle", _ => "SQLite",
        };
        var sb = new StringBuilder();
        sb.Append($"# {name}\n\n");
        sb.Append($"Web API generated by Stackwright from the `{req.Connection.Database ?? Path.GetFileName(req.Connection.Server)}` database ({providerName}), .NET {target.Major}.\n\n");
        sb.Append("## Architecture\n\n");
        sb.Append("Clean Architecture: each layer depends only on the layers inside it.\n\n");
        sb.Append("```\n");
        sb.Append($"{name}.Api            → HTTP: {(req.Style == EndpointStyle.MinimalApi ? "minimal API endpoints" : "controllers")}{(jwt ? ", sign-in" : "")}, API docs\n");
        sb.Append($"  {name}.Infrastructure → EF Core (AppDbContext, configurations, repository){(jwt ? ", JWT tokens" : "")}\n");
        sb.Append($"    {name}.Application  → use cases: services, DTOs, request validation, interfaces\n");
        sb.Append($"      {name}.Domain     → entities, no dependencies\n");
        sb.Append("```\n\n");
        sb.Append("- Add business rules in `Application/Features/<Resource>/<Entity>Service.cs`.\n");
        sb.Append("- Change table/column mappings in `Infrastructure/Persistence/Configurations`.\n\n");
        sb.Append("## Run\n\n```\ndotnet run --project src/" + name + ".Api\n```\n\n");
        if (req.IncludeApiDocs) sb.Append($"Interactive API docs: `/{(target.BuiltInOpenApi ? "scalar" : "swagger")}`.\n\n");
        if (jwt)
        {
            sb.Append("## Sign-in (JWT)\n\n");
            sb.Append("All resource endpoints require a bearer token.\n\n");
            sb.Append("1. `POST /api/auth/login` with `{ \"userName\": \"admin\", \"password\": \"…\" }`. The admin password was shown once by Stackwright when the project was created.\n");
            sb.Append("2. Send the returned `accessToken` as `Authorization: Bearer <token>`.\n\n");
            sb.Append("Users are listed under `Auth:Users` in `appsettings.json` with hashed passwords (`PasswordHasher.Hash`). ");
            sb.Append("Before going to production, move `Jwt:Key` and the connection string to environment variables (`Jwt__Key`, `ConnectionStrings__Default`) or a secret store.\n\n");
        }
        sb.Append("## Resources\n\n");
        foreach (var p in plans) sb.Append($"- `{Route(p)}` → `{p.Table.Schema}.{p.Table.Name}` ({string.Join(", ", p.Operations)})\n");
        return Norm(sb.ToString());
    }
}
