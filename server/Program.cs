using QosriStudio.Server.Services.Db;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using QosriStudio.Server.Codegen;
using QosriStudio.Server.Models;
using QosriStudio.Server.Services;
using QosriStudio.Server.Services.Frontend;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5199");

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(origin => new Uri(origin).IsLoopback)
    .AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddSingleton<FileSystemService>();
builder.Services.AddSingleton<RecentProjectsStore>();
builder.Services.AddSingleton<HistoryService>();
builder.Services.AddSingleton<DotnetService>();
builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<EndpointWriter>();
builder.Services.AddSingleton<ProjectGenerator>();
builder.Services.AddSingleton<UpgradeService>();
builder.Services.AddSingleton<JobService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<DeployService>();
builder.Services.AddSingleton<DecompileService>();
builder.Services.AddSingleton<QosriStudio.Server.Services.Frontend.FrontendService>();
builder.Services.AddSingleton<QosriStudio.Server.Services.Db.DbStudioService>();
builder.Services.AddSingleton<GitService>();
builder.Services.AddSingleton<ProjectFilesService>();
builder.Services.AddSingleton<TerminalService>();

var app = builder.Build();

// Friendly error messages for the UI (never stack traces)
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (StudioException ex)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "A file couldn't be read or written: " + ex.Message });
    }
    catch (Exception ex) when (!ctx.Response.HasStarted)
    {
        app.Logger.LogError(ex, "Unhandled error");
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = "Something unexpected went wrong: " + ex.Message });
    }
});

app.UseCors();

// Serve the built React app when it exists (npm run build → server/wwwroot)
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

// ---------------------------------------------------------------- System
api.MapGet("/system", async (DotnetService dotnet) =>
{
    var tc = await dotnet.GetToolchainAsync();
    var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    return new
    {
        dotnetVersion = tc.SdkVersion,
        sdks = tc.SdkMajors,
        runtimes = tc.AspNetRuntimeMajors,
        hasVisualStudio = tc.MsBuildPath is not null,
        hasIisExpress = tc.IisExpressPath is not null,
        targets = await dotnet.TargetOptionsAsync(),
        defaultLocation = Directory.Exists(Path.Combine(docs, "source", "repos")) ? Path.Combine(docs, "source", "repos") : docs,
    };
});

// ---------------------------------------------------------------- File system
api.MapGet("/fs/browse", (string? path, string? ext, FileSystemService fs) => fs.Browse(path, ext));
api.MapGet("/fs/find-projects", (string root, FileSystemService fs) => fs.FindProjects(root));
api.MapGet("/fs/solution", (string path, FileSystemService fs) => fs.SolutionProjects(path));

api.MapPost("/fs/reveal", (PathBody body) =>
{
    var target = File.Exists(body.Path) ? Path.GetDirectoryName(body.Path)! : body.Path;
    if (!Directory.Exists(target)) throw new StudioException("That folder no longer exists.");
    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
    return Results.Ok();
});

api.MapPost("/fs/open-editor", (PathBody body) =>
{
    // A source file with a line opens at that line; a project opens its folder
    var args = body.Line is > 0 && File.Exists(body.Path) && body.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
        ? $"-g \"{body.Path}:{body.Line}\""
        : $"\"{(File.Exists(body.Path) ? Path.GetDirectoryName(body.Path)! : body.Path)}\"";
    try
    {
        Process.Start(new ProcessStartInfo("code", args) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
    }
    catch
    {
        throw new StudioException("Visual Studio Code wasn't found on this computer.");
    }
    return Results.Ok();
});

// ---------------------------------------------------------------- Projects
api.MapGet("/projects/recent", (RecentProjectsStore store) => store.GetAll());
api.MapDelete("/projects/recent", (string path, RecentProjectsStore store) => { store.Remove(path); return Results.Ok(); });

api.MapGet("/project", async (string path, RecentProjectsStore recent, HistoryService history, DotnetService dotnet) =>
{
    var model = ProjectAnalyzer.Analyze(path);
    recent.Touch(model.CsprojPath, model.Name, model.Endpoints.Count);
    return await ToDetails(model, history, dotnet);
});

api.MapPost("/project/endpoints", async (string path, AddEndpointRequest req, EndpointWriter writer, HistoryService history, DotnetService dotnet) =>
{
    var message = writer.Add(path, req);
    return new { message, project = await ToDetails(ProjectAnalyzer.Analyze(path), history, dotnet) };
});

api.MapPut("/project/endpoints/{id}", async (string path, string id, UpdateEndpointRequest req, EndpointWriter writer, HistoryService history, DotnetService dotnet) =>
{
    var message = writer.Update(path, id, req);
    return new { message, project = await ToDetails(ProjectAnalyzer.Analyze(path), history, dotnet) };
});

api.MapDelete("/project/endpoints/{id}", async (string path, string id, EndpointWriter writer, HistoryService history, DotnetService dotnet) =>
{
    var message = writer.Delete(path, id);
    return new { message, project = await ToDetails(ProjectAnalyzer.Analyze(path), history, dotnet) };
});

api.MapPost("/project/undo", async (string path, HistoryService history, DotnetService dotnet) =>
{
    var what = history.UndoLast(path);
    return new { message = $"Undone: {what}", project = await ToDetails(ProjectAnalyzer.Analyze(path), history, dotnet) };
});

api.MapGet("/project/health", async (string path, DotnetService dotnet) =>
{
    var model = ProjectAnalyzer.Analyze(path);
    return HealthService.Check(model, await dotnet.CapabilitiesAsync(model));
});

api.MapPost("/project/upgrade/plan", (string path, UpgradeRequest req, UpgradeService upgrade, CancellationToken ct) => upgrade.PlanAsync(path, req, ct));
api.MapPost("/project/upgrade", async (string path, UpgradeRequest req, UpgradeService upgrade, HistoryService history, DotnetService dotnet, CancellationToken ct) =>
{
    var message = await upgrade.ApplyAsync(path, req, ct);
    return new { message, project = await ToDetails(ProjectAnalyzer.Analyze(path), history, dotnet) };
});

api.MapPost("/project/sql/test", (string path, SqlTestRequest req, CancellationToken ct) => SqlTestService.RunAsync(ProjectAnalyzer.Analyze(path), req, ct));
api.MapPost("/sql/parameters", (SqlTextBody body) => EndpointWriter.SqlParameterNames(body.Sql ?? ""));

api.MapPost("/project/build", (string path, DotnetService dotnet, CancellationToken ct) => dotnet.BuildAsync(path, ct));

api.MapGet("/project/run", (string path, DotnetService dotnet) => dotnet.Status(path));
api.MapPost("/project/run", (string path, DotnetService dotnet) => dotnet.Start(path));
api.MapPost("/project/stop", (string path, DotnetService dotnet) => dotnet.Stop(path));
api.MapPost("/project/try", (string path, TryRequest req, DotnetService dotnet, CancellationToken ct) => dotnet.TryAsync(path, req, ct));

api.MapGet("/project/postman", (string path, string? baseUrl) =>
{
    var model = ProjectAnalyzer.Analyze(path);
    var json = PostmanExporter.Export(model, baseUrl);
    return Results.File(Encoding.UTF8.GetBytes(json), "application/json", $"{model.Name}.postman_collection.json");
});

api.MapGet("/solution", (string path, RecentProjectsStore recent) =>
{
    var details = SolutionService.Load(path);
    recent.Touch(details.Path, details.Name, details.Projects.Sum(p => p.EndpointCount ?? 0));
    return details;
});

// ---------------------------------------------------------------- Settings
api.MapGet("/project/settings", (string path, SettingsService settings) => settings.Load(path));
api.MapPut("/project/settings", (string path, SaveSettingsRequest req, SettingsService settings) => new { message = settings.Save(path, req), settings = settings.Load(path) });
api.MapPost("/project/settings/environment", (string path, NameBody body, SettingsService settings) => new { message = settings.AddEnvironment(path, body.Name), settings = settings.Load(path) });

// ---------------------------------------------------------------- Publish, deploy & background tasks
api.MapGet("/deploy/tools", (DeployService deploy) => deploy.ToolsAsync());
api.MapGet("/project/deploy/profiles", (string path, DeployService deploy) => deploy.Profiles(path));
api.MapPost("/project/deploy", (string path, DeployRequest req, DeployService deploy) => new { jobId = deploy.Start(path, req) });
api.MapPost("/solution/build", (string path, DeployService deploy) => new { jobId = deploy.BuildSolution(path) });
api.MapGet("/jobs", (string owner, JobService jobs) => jobs.ForOwner(Path.GetFullPath(owner)));
api.MapGet("/jobs/{id}", (string id, JobService jobs) => jobs.Get(id));
api.MapPost("/jobs/{id}/cancel", (string id, JobService jobs) => { jobs.Cancel(id); return Results.Ok(); });

// ---------------------------------------------------------------- Recover code from published files
api.MapPost("/decompile/inspect", (PathBody body, DecompileService d) => d.Inspect(body.Path));
api.MapPost("/decompile/recover", (RecoverRequest req, DecompileService d) => new { jobId = d.Recover(req) });

// ---------------------------------------------------------------- Angular / React / Next.js / Vue projects
api.MapGet("/frontend", (string path, FrontendService fe, RecentProjectsStore recent) =>
{
    var d = fe.Details(path);
    recent.Touch(d.Path, d.Name, d.ApiCalls.Count);
    return d;
});
api.MapPost("/frontend/link", (string path, LinkBody body, FrontendService fe) => { fe.Link(FrontendService.NormalizePath(path), body.Backend); return fe.Details(path); });
api.MapGet("/frontend/settings", (string path, FrontendService fe) => fe.Settings(path));
api.MapPut("/frontend/settings", (string path, SaveSettingsRequest req, FrontendService fe) => new { message = fe.SaveSettings(path, req), settings = fe.Settings(path) });
api.MapPost("/frontend/settings/environment", (string path, NameBody body, FrontendService fe) => new { message = fe.AddEnvironment(path, body.Name), settings = fe.Settings(path) });
api.MapPost("/frontend/install", (string path, FrontendService fe) => new { jobId = fe.Install(path) });
api.MapPost("/frontend/build", (string path, FrontendService fe) => new { jobId = fe.Build(path) });
api.MapGet("/frontend/run", (string path, FrontendService fe) => fe.DevStatus(path));
api.MapPost("/frontend/run", (string path, FrontendService fe) => fe.StartDev(path));
api.MapPost("/frontend/stop", (string path, FrontendService fe) => fe.StopDev(path));
api.MapPost("/frontend/undo", (string path, FrontendService fe, HistoryService history) =>
{
    var what = history.UndoLast(FrontendService.NormalizePath(path));
    return new { message = $"Undone: {what}", project = fe.Details(path) };
});
api.MapGet("/frontend/client", (string path, FrontendService fe) => fe.PreviewClient(path));
api.MapPost("/frontend/client", (PathBody b, FrontendService fe) => new { message = fe.GenerateClient(b.Path) });
api.MapGet("/project/openapi", (string path, string? server) =>
{
    var model = ProjectAnalyzer.Analyze(path);
    var json = QosriStudio.Server.Services.Contracts.OpenApiExporter.Export(QosriStudio.Server.Services.Contracts.ApiContract.From(model), server);
    return Results.File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", $"{model.Name}.openapi.json");
});
api.MapGet("/frontend/health", (string path, FrontendService fe) => fe.Health(path));




// ---------------------------------------------------------------- Project files
var files = api.MapGroup("/files");
files.MapGet("/list", (string root, string? dir, bool? hidden, ProjectFilesService f) => f.List(root, dir, hidden ?? false));
files.MapGet("/read", (string root, string path, ProjectFilesService f) => f.Read(root, path));
files.MapGet("/raw", (string root, string path) =>
{
    var full = Path.GetFullPath(Path.Combine(ProjectFilesService.RootOf(root), path));
    if (!full.StartsWith(ProjectFilesService.RootOf(root), StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return Results.NotFound();
    var type = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider().TryGetContentType(full, out var t) ? t : "application/octet-stream";
    return Results.File(full, type);
});
files.MapPost("/write", (FileWriteBody b, ProjectFilesService f) => f.Write(b));
files.MapPost("/create", (FileCreateBody b, ProjectFilesService f) => f.Create(b));
files.MapPost("/rename", (FileRenameBody b, ProjectFilesService f) => f.Rename(b));
files.MapPost("/delete", (FileDeleteBody b, ProjectFilesService f) => new { message = f.Delete(b) });
files.MapGet("/search", (string root, string q, bool? content, ProjectFilesService f) => f.Search(root, q, content ?? true));
files.MapGet("/history", (string root, HistoryService h) => h.GetEntries(root));
files.MapPost("/undo", (PathBody b, HistoryService h) => new { message = "Undone: " + h.UndoLast(b.Path) });

// ---------------------------------------------------------------- Terminal
var term = api.MapGroup("/terminal");
term.MapGet("/shells", () => TerminalService.Shells());
term.MapPost("/start", (TerminalStartBody b, TerminalService t) => t.Start(b));
term.MapGet("/{id}/output", (string id, long? since, TerminalService t) => t.Output(id, since ?? 0));
term.MapPost("/{id}/input", (string id, TerminalInputBody b, TerminalService t) => { t.Input(id, b.Text); return Results.Ok(); });
term.MapPost("/{id}/stop", (string id, TerminalService t) => { t.Stop(id); return Results.Ok(); });
term.MapDelete("/{id}", (string id, TerminalService t) => { t.Close(id); return Results.Ok(); });

// ---------------------------------------------------------------- Source control (Git)
var git = api.MapGroup("/git");
git.MapGet("/status", (string path, GitService g) => g.Status(path));
git.MapPost("/init", (GitPathBody b, GitService g) => g.Init(b.Path));
git.MapPost("/gitignore", (GitPathBody b, GitService g) => { g.AddGitignore(b.Path); return Results.Ok(); });
git.MapPost("/identity", (GitIdentityBody b, GitService g) => { g.SetIdentity(b); return Results.Ok(); });
git.MapPost("/trust", (GitPathBody b, GitService g) => { g.Trust(b.Path); return Results.Ok(); });
git.MapPost("/remote", (GitRemoteBody b, GitService g) => { g.SetRemote(b); return Results.Ok(); });
git.MapPost("/remote/remove", (GitRemoteBody b, GitService g) => { g.RemoveRemote(b.Path, b.Name); return Results.Ok(); });
git.MapPost("/stage", (GitFilesBody b, GitService g) => { g.Stage(b); return Results.Ok(); });
git.MapPost("/unstage", (GitFilesBody b, GitService g) => { g.Unstage(b); return Results.Ok(); });
git.MapPost("/commit", (GitCommitBody b, GitService g) => g.Commit(b));
git.MapPost("/discard", (GitFilesBody b, GitService g) => { g.Discard(b); return Results.Ok(); });
git.MapGet("/diff", (string path, string file, string? commit, GitService g) => g.Diff(path, file, commit));
git.MapPost("/resolve", (GitResolveBody b, GitService g) => { g.Resolve(b); return Results.Ok(); });
git.MapPost("/abort", (GitPathBody b, GitService g) => { g.AbortOperation(b.Path); return Results.Ok(); });
git.MapGet("/log", (string path, int? skip, string? branch, string? search, string? file, GitService g) => g.Log(path, skip ?? 0, branch, search, file));
git.MapGet("/show", (string path, string hash, GitService g) => g.Show(path, hash));
git.MapPost("/revert", (GitRefBody b, GitService g) => { g.Revert(b); return Results.Ok(); });
git.MapPost("/undo-commit", (GitPathBody b, GitService g) => { g.UndoLastCommit(b.Path); return Results.Ok(); });
git.MapPost("/tag", (GitRefBody b, GitService g) => { g.Tag(b); return Results.Ok(); });
git.MapGet("/branches", (string path, GitService g) => g.Branches(path));
git.MapPost("/branch/create", (GitNameBody b, GitService g) => { g.CreateBranch(b); return Results.Ok(); });
git.MapPost("/branch/checkout", (GitNameBody b, GitService g) => { g.Checkout(b); return Results.Ok(); });
git.MapPost("/branch/delete", (GitNameBody b, GitService g) => { g.DeleteBranch(b); return Results.Ok(); });
git.MapPost("/branch/rename", (GitNameBody b, GitService g) => { g.RenameBranch(b); return Results.Ok(); });
git.MapPost("/merge", (GitNameBody b, GitService g) => g.Merge(b));
git.MapGet("/stashes", (string path, GitService g) => g.Stashes(path));
git.MapPost("/stash", (GitRefBody b, GitService g) => { g.Stash(b); return Results.Ok(); });
git.MapPost("/stash/apply", (GitRefBody b, GitService g) => { g.StashApply(b, false); return Results.Ok(); });
git.MapPost("/stash/pop", (GitRefBody b, GitService g) => { g.StashApply(b, true); return Results.Ok(); });
git.MapPost("/stash/drop", (GitRefBody b, GitService g) => { g.StashDrop(b); return Results.Ok(); });
git.MapPost("/fetch", (GitPathBody b, GitService g) => new { jobId = g.Fetch(b.Path) });
git.MapPost("/pull", (GitPathBody b, GitService g) => new { jobId = g.Pull(b.Path) });
git.MapPost("/push", (GitPathBody b, bool? tags, GitService g) => new { jobId = g.Push(b.Path, tags ?? true) });
git.MapPost("/clone", (GitCloneBody b, GitService g) => new { jobId = g.Clone(b) });

// ---------------------------------------------------------------- Database Studio
var dbs = api.MapGroup("/dbs");
dbs.MapGet("/connections", (DbStudioService s) => s.Connections());
dbs.MapPost("/connections", (SaveConnectionRequest req, DbStudioService s) => s.SaveConnection(req));
dbs.MapDelete("/connections/{id}", (string id, DbStudioService s) => { s.DeleteConnection(id); return Results.Ok(); });
dbs.MapPost("/connect", (DbRef r, DbStudioService s, CancellationToken ct) => s.ConnectAsync(r, ct));
dbs.MapPost("/schema", (DbRef r, DbStudioService s, CancellationToken ct) => s.SchemaAsync(r, ct));
dbs.MapPost("/script", async (ScriptBody b, DbStudioService s, CancellationToken ct) => new { text = await s.ScriptAsync(b.Ref, b.Schema, b.Table, ct) });
dbs.MapPost("/routine", async (RoutineRequest req, DbStudioService s, CancellationToken ct) => new { text = await s.RoutineAsync(req, ct) });
dbs.MapPost("/rows", (RowsRequest req, DbStudioService s, CancellationToken ct) => s.RowsAsync(req, ct));
dbs.MapPost("/rows/insert", (RowChange req, DbStudioService s, CancellationToken ct) => s.InsertAsync(req, ct));
dbs.MapPost("/rows/update", (RowChange req, DbStudioService s, CancellationToken ct) => s.UpdateAsync(req, ct));
dbs.MapPost("/rows/delete", (RowChange req, DbStudioService s, CancellationToken ct) => s.DeleteAsync(req, ct));
dbs.MapPost("/rows/import", (ImportRequest req, DbStudioService s, CancellationToken ct) => s.ImportAsync(req, ct));
dbs.MapPost("/rows/export", async (ExportRequest req, DbStudioService s, CancellationToken ct) =>
{
    var (content, name, type) = await s.ExportAsync(req, ct);
    return Results.File(System.Text.Encoding.UTF8.GetBytes(content), type + "; charset=utf-8", name);
});
dbs.MapPost("/ddl/preview", (DdlRequest req, DbStudioService s, CancellationToken ct) => s.PreviewAsync(req, ct));
dbs.MapPost("/ddl/apply", (DdlRequest req, DbStudioService s, CancellationToken ct) => s.ApplyDdlAsync(req, ct));
dbs.MapPost("/extras", (DbRef r, DbStudioService s, CancellationToken ct) => s.ExtrasAsync(r, ct));
dbs.MapPost("/routine/details", (RoutineRequest req, DbStudioService s, CancellationToken ct) => s.RoutineDetailsAsync(req, ct));
dbs.MapPost("/routine/run", (ExecRoutineRequest req, DbStudioService s, CancellationToken ct) => s.ExecuteRoutineAsync(req, ct));
dbs.MapPost("/trigger", async (TriggerRequest req, DbStudioService s, CancellationToken ct) => new { text = await s.TriggerAsync(req, ct) });
dbs.MapPost("/grants", (TableRefRequest req, DbStudioService s, CancellationToken ct) => s.GrantsAsync(req, ct));
dbs.MapPost("/backup", async (BackupRequest req, DbStudioService s, CancellationToken ct) => new { message = await s.BackupAsync(req, ct) });
dbs.MapPost("/script-db", async (ScriptDbRequest req, DbStudioService s, CancellationToken ct) =>
{
    var (content, name) = await s.ScriptDatabaseAsync(req, ct);
    return Results.File(System.Text.Encoding.UTF8.GetBytes(content), "text/plain; charset=utf-8", name);
});
dbs.MapPost("/sqlite/new", (PathBody b) => new { path = DbStudioService.CreateSqliteFile(b.Path) });
dbs.MapPost("/sql", (SqlRequest req, DbStudioService s, CancellationToken ct) => s.RunSqlAsync(req, ct));

// ---------------------------------------------------------------- Database
api.MapPost("/db/connect", async (DbConnectionInfo info, DatabaseService db, CancellationToken ct) =>
{
    var (version, databases) = await db.TestAsync(info, ct);
    return new { version, databases };
});

api.MapPost("/db/tables", (DbConnectionInfo info, DatabaseService db, CancellationToken ct) => db.GetTablesAsync(info, ct));

api.MapPost("/db/generate", async (GenerateProjectRequest req, ProjectGenerator gen, RecentProjectsStore recent, CancellationToken ct) =>
{
    var result = await gen.GenerateAsync(req, ct);
    var model = ProjectAnalyzer.Analyze(result.ProjectPath);
    recent.Touch(model.CsprojPath, model.Name, model.Endpoints.Count);
    return result;
});

// SPA fallback for client-side routes
app.MapFallbackToFile("index.html");

app.Lifetime.ApplicationStopping.Register(() =>
{
    app.Services.GetRequiredService<DotnetService>().Dispose();
    app.Services.GetRequiredService<FrontendService>().StopAll();
    app.Services.GetRequiredService<TerminalService>().CloseAll();
});

app.Logger.LogInformation("QosriStudio server ready.");
app.Run();

static async Task<ProjectDetails> ToDetails(ProjectModel m, HistoryService history, DotnetService dotnet) => new(
    m.Name,
    m.CsprojPath,
    m.Directory,
    m.TargetFramework,
    await dotnet.CapabilitiesAsync(m),
    m.RootNamespace,
    m.PreferredStyle,
    m.Endpoints.Select(e => e.Info).ToList(),
    m.Groups.Select(g => new EndpointGroup(g.Key, g.Name, g.Style, g.BaseRoute, g.File.Relative, g.Endpoints.Count)).ToList(),
    m.Entities,
    m.Packages,
    history.GetEntries(m.CsprojPath),
    m.Warnings,
    m.SolutionPath,
    m.ReferencedProjects,
    m.BinaryReferences,
    m.Services,
    new DataAccessInfo(m.SqlProviders, m.ConnectionStrings));

record ScriptBody(DbRef Ref, string Schema, string Table);
record PathBody(string Path, int? Line = null);
record SqlTextBody(string? Sql);
record NameBody(string Name);
record LinkBody(string? Backend);
