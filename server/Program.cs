using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using EndpointStudio.Server.Codegen;
using EndpointStudio.Server.Models;
using EndpointStudio.Server.Services;

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
api.MapGet("/fs/browse", (string? path, FileSystemService fs) => fs.Browse(path));
api.MapGet("/fs/find-projects", (string root, FileSystemService fs) => fs.FindProjects(root));

api.MapPost("/fs/reveal", (PathBody body) =>
{
    var target = File.Exists(body.Path) ? Path.GetDirectoryName(body.Path)! : body.Path;
    if (!Directory.Exists(target)) throw new StudioException("That folder no longer exists.");
    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
    return Results.Ok();
});

api.MapPost("/fs/open-editor", (PathBody body) =>
{
    var target = File.Exists(body.Path) ? Path.GetDirectoryName(body.Path)! : body.Path;
    try
    {
        Process.Start(new ProcessStartInfo("code", $"\"{target}\"") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
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

app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<DotnetService>().Dispose());

app.Logger.LogInformation("Endpoint Studio server ready.");
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
    m.Warnings);

record PathBody(string Path);
