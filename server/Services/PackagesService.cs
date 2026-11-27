using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QosriStudio.Server.Models;

namespace QosriStudio.Server.Services;

public record PackageItem(string Id, string? Requested, string? Installed, string? Latest, string? SafeLatest, bool Dev, bool Transitive, List<string> Vulnerabilities, string? Project);
public record PackageList(string Kind, string Manager, List<PackageItem> Packages, bool CheckedUpdates, List<string> Notes);
public record PackageSearchItem(string Id, string Version, string? Description, long? Downloads, bool Verified);
public record PackageChangeBody(string Path, string Id, string? Version, bool Dev = false);

/// <summary>NuGet packages of .NET projects and npm / pnpm / yarn packages of front-end apps.</summary>
public class PackagesService(JobService jobs)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20), DefaultRequestHeaders = { { "User-Agent", "QosriStudio" } } };

    private static bool IsNpm(string path) => path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase) || Directory.Exists(path) && File.Exists(Path.Combine(path, "package.json"));

    private static string PackageJson(string path) => path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase) ? path : Path.Combine(path, "package.json");

    // ------------------------------------------------------------------ list

    public async Task<PackageList> ListAsync(string path, bool updates, CancellationToken ct) =>
        IsNpm(path) ? await NpmListAsync(PackageJson(path), updates, ct) : await NuGetListAsync(path, updates, ct);

    private static async Task<(int Exit, string Out, string Err)> RunAsync(string file, IEnumerable<string> args, string dir, CancellationToken ct, int timeoutSec = 180)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["NO_COLOR"] = "1";
        using var p = Process.Start(psi) ?? throw new StudioException($"{file} couldn't be started.");
        var o = p.StandardOutput.ReadToEndAsync(ct);
        var e = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new StudioException("This took too long and was stopped."); }
        return (p.ExitCode, await o, await e);
    }

    private static async Task<PackageList> NuGetListAsync(string path, bool updates, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new StudioException("That project or solution no longer exists.");
        var dir = Path.GetDirectoryName(full)!;
        if (File.Exists(Path.Combine(dir, "packages.config")))
            return new PackageList("nuget", "packages.config", ReadPackagesConfig(Path.Combine(dir, "packages.config")), false,
                ["This project uses packages.config (classic .NET Framework). Update its packages from Visual Studio, or migrate it to PackageReference."]);

        var notes = new List<string>();
        async Task<JsonNode?> List(params string[] extra)
        {
            var (exit, o, e) = await RunAsync("dotnet", ["list", full, "package", "--format", "json", .. extra], dir, ct);
            var start = o.IndexOf('{');
            if (start < 0)
            {
                var msg = (e + o).Trim();
                if (msg.Contains("restore", StringComparison.OrdinalIgnoreCase) || msg.Contains("assets", StringComparison.OrdinalIgnoreCase))
                {
                    await RunAsync("dotnet", ["restore", full], dir, ct, 300);
                    (exit, o, e) = await RunAsync("dotnet", ["list", full, "package", "--format", "json", .. extra], dir, ct);
                    start = o.IndexOf('{');
                }
                if (start < 0) throw new StudioException("The package list couldn't be read: " + Regex.Replace((e + o).Trim(), @"\s+", " "));
            }
            return JsonNode.Parse(o[start..]);
        }

        var installed = await List();
        var items = new Dictionary<string, PackageItem>(StringComparer.OrdinalIgnoreCase);
        void Walk(JsonNode? root, Action<string, JsonNode, bool> each)
        {
            foreach (var proj in root?["projects"]?.AsArray() ?? [])
            {
                var projName = Path.GetFileNameWithoutExtension(proj?["path"]?.GetValue<string>() ?? "");
                foreach (var fw in proj?["frameworks"]?.AsArray() ?? [])
                {
                    foreach (var p in fw?["topLevelPackages"]?.AsArray() ?? []) if (p is not null) each(projName, p, false);
                    foreach (var p in fw?["transitivePackages"]?.AsArray() ?? []) if (p is not null) each(projName, p, true);
                }
            }
        }
        var multi = (installed?["projects"]?.AsArray().Count ?? 0) > 1;
        Walk(installed, (proj, p, transitive) =>
        {
            var id = p["id"]!.GetValue<string>();
            var key = multi ? proj + "|" + id : id;
            items.TryAdd(key, new PackageItem(id, p["requestedVersion"]?.GetValue<string>(), p["resolvedVersion"]?.GetValue<string>(), null, null, false, transitive, [], multi ? proj : null));
        });

        if (updates)
        {
            var latest = await List("--outdated");
            var safe = await List("--outdated", "--highest-minor");
            var vuln = await List("--vulnerable");
            Walk(latest, (proj, p, _) => Update(proj, p, (i, v) => i with { Latest = v }, "latestVersion"));
            Walk(safe, (proj, p, _) => Update(proj, p, (i, v) => i with { SafeLatest = v }, "latestVersion"));
            Walk(vuln, (proj, p, _) =>
            {
                var key = multi ? proj + "|" + p["id"]!.GetValue<string>() : p["id"]!.GetValue<string>();
                if (!items.TryGetValue(key, out var i)) return;
                var list = p["vulnerabilities"]?.AsArray().Select(v => $"{v?["severity"]?.GetValue<string>()} – {v?["advisoryurl"]?.GetValue<string>()}").ToList() ?? [];
                items[key] = i with { Vulnerabilities = list };
            });
            foreach (var problem in new[] { latest, safe, vuln }.SelectMany(r => r?["problems"]?.AsArray() ?? []))
                if (problem?["text"]?.GetValue<string>() is { } t && !notes.Contains(t)) notes.Add(t);
        }
        return new PackageList("nuget", "NuGet", items.Values.OrderBy(i => i.Transitive).ThenBy(i => i.Project).ThenBy(i => i.Id, StringComparer.OrdinalIgnoreCase).ToList(), updates, notes);

        void Update(string proj, JsonNode p, Func<PackageItem, string, PackageItem> set, string field)
        {
            var key = multi ? proj + "|" + p["id"]!.GetValue<string>() : p["id"]!.GetValue<string>();
            var v = p[field]?.GetValue<string>();
            if (v is null || !items.TryGetValue(key, out var i)) return;
            if (v == i.Installed || v.Contains("Not found", StringComparison.OrdinalIgnoreCase)) return;
            items[key] = set(i, v);
        }
    }

    private static List<PackageItem> ReadPackagesConfig(string file)
    {
        try
        {
            return System.Xml.Linq.XDocument.Load(file).Descendants("package")
                .Select(e => new PackageItem((string?)e.Attribute("id") ?? "", (string?)e.Attribute("version"), (string?)e.Attribute("version"), null, null, false, false, [], null)).ToList();
        }
        catch { return []; }
    }

    private static string Manager(string dir) =>
        File.Exists(Path.Combine(dir, "pnpm-lock.yaml")) ? "pnpm" : File.Exists(Path.Combine(dir, "yarn.lock")) ? "yarn" : File.Exists(Path.Combine(dir, "bun.lockb")) || File.Exists(Path.Combine(dir, "bun.lock")) ? "bun" : "npm";

    private static async Task<PackageList> NpmListAsync(string packageJson, bool updates, CancellationToken ct)
    {
        if (!File.Exists(packageJson)) throw new StudioException("package.json no longer exists.");
        var dir = Path.GetDirectoryName(packageJson)!;
        var pkg = JsonNode.Parse(await File.ReadAllTextAsync(packageJson, ct));
        var items = new List<PackageItem>();
        foreach (var (section, dev) in new[] { ("dependencies", false), ("devDependencies", true), ("peerDependencies", false) })
        {
            if (pkg?[section] is not JsonObject deps) continue;
            foreach (var (id, v) in deps)
            {
                if (items.Any(i => i.Id == id)) continue;
                string? installed = null;
                var mod = Path.Combine(dir, "node_modules", id.Replace('/', Path.DirectorySeparatorChar), "package.json");
                if (File.Exists(mod)) try { installed = JsonNode.Parse(await File.ReadAllTextAsync(mod, ct))?["version"]?.GetValue<string>(); } catch { }
                items.Add(new PackageItem(id, v?.ToString(), installed, null, null, dev, false, [], null));
            }
        }
        var notes = new List<string>();
        if (!Directory.Exists(Path.Combine(dir, "node_modules"))) notes.Add("Packages aren't installed yet. Use “Install packages”.");
        if (updates && items.Count > 0)
        {
            // Ask the npm registry for the newest version, and the newest one with the same major version
            using var gate = new SemaphoreSlim(12);
            var lookups = items.Select(async item =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var doc = await Http.GetFromJsonAsync<JsonNode>($"https://registry.npmjs.org/{Uri.EscapeDataString(item.Id).Replace("%40", "@")}", ct);
                    var latest = doc?["dist-tags"]?["latest"]?.GetValue<string>();
                    var current = item.Installed ?? Regex.Match(item.Requested ?? "", @"\d+\.\d+\.\d+").Value;
                    string? safe = null;
                    if (current.Length > 0 && doc?["versions"] is JsonObject versions)
                    {
                        var major = current.Split('.')[0];
                        safe = versions.Select(v => v.Key).Where(v => !v.Contains('-') && v.Split('.')[0] == major).OrderByDescending(v => Version.TryParse(Regex.Replace(v, @"[^\d.]", ""), out var x) ? x : new Version()).FirstOrDefault();
                    }
                    return item with { Latest = latest != current ? latest : null, SafeLatest = safe != current ? safe : null };
                }
                catch { return item; }
                finally { gate.Release(); }
            });
            items = (await Task.WhenAll(lookups)).ToList();
            if (Manager(dir) == "npm" && Directory.Exists(Path.Combine(dir, "node_modules")))
            {
                try
                {
                    var (_, o, _) = await RunAsync(OperatingSystem.IsWindows() ? "npm.cmd" : "npm", ["audit", "--json"], dir, ct, 120);
                    var audit = JsonNode.Parse(o[o.IndexOf('{')..]);
                    if (audit?["vulnerabilities"] is JsonObject vulns)
                        items = items.Select(i => vulns[i.Id] is { } v ? i with { Vulnerabilities = [$"{v["severity"]} – {string.Join(", ", (v["via"]?.AsArray() ?? []).Select(x => x is JsonObject xo ? xo["title"]?.ToString() : x?.ToString()).Where(x => x is not null).Distinct().Take(3))}"] } : i).ToList();
                }
                catch { notes.Add("The security check (npm audit) couldn't run."); }
            }
        }
        return new PackageList("npm", Manager(dir), items.OrderBy(i => i.Dev).ThenBy(i => i.Id, StringComparer.OrdinalIgnoreCase).ToList(), updates, notes);
    }

    // ------------------------------------------------------------------ search

    public async Task<List<PackageSearchItem>> SearchAsync(string path, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        try
        {
            if (IsNpm(path))
            {
                var r = await Http.GetFromJsonAsync<JsonNode>($"https://registry.npmjs.org/-/v1/search?text={Uri.EscapeDataString(query)}&size=20", ct);
                return r?["objects"]?.AsArray().Select(o => new PackageSearchItem(o?["package"]?["name"]?.ToString() ?? "", o?["package"]?["version"]?.ToString() ?? "",
                    o?["package"]?["description"]?.ToString(), (long?)(o?["downloads"]?["weekly"]?.GetValue<double?>()), false)).ToList() ?? [];
            }
            var n = await Http.GetFromJsonAsync<JsonNode>($"https://azuresearch-usnc.nuget.org/query?q={Uri.EscapeDataString(query)}&take=20&prerelease=false&semVerLevel=2.0.0", ct);
            return n?["data"]?.AsArray().Select(d => new PackageSearchItem(d?["id"]?.ToString() ?? "", d?["version"]?.ToString() ?? "", d?["description"]?.ToString(),
                d?["totalDownloads"]?.GetValue<long>(), d?["verified"]?.GetValue<bool>() ?? false)).ToList() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { throw new StudioException("The package catalog couldn't be reached. Check the internet connection."); }
    }

    public async Task<List<string>> VersionsAsync(string path, string id, CancellationToken ct)
    {
        try
        {
            if (IsNpm(path))
            {
                var doc = await Http.GetFromJsonAsync<JsonNode>($"https://registry.npmjs.org/{Uri.EscapeDataString(id).Replace("%40", "@")}", ct);
                return (doc?["versions"] as JsonObject)?.Select(v => v.Key).Reverse().Take(60).ToList() ?? [];
            }
            var idx = await Http.GetFromJsonAsync<JsonNode>($"https://api.nuget.org/v3-flatcontainer/{id.ToLowerInvariant()}/index.json", ct);
            return idx?["versions"]?.AsArray().Select(v => v!.ToString()).Reverse().Take(60).ToList() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return []; }
    }

    // ------------------------------------------------------------------ add / update / remove (shown live)

    private string Job(string owner, string title, string file, List<string> args, string dir) => jobs.Start(owner, "packages", title, async ctx =>
    {
        ctx.Log("> " + Path.GetFileNameWithoutExtension(file) + " " + string.Join(' ', args));
        var psi = new ProcessStartInfo(file) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        using var p = Process.Start(psi)!;
        var errors = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) ctx.Log(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) { ctx.Log(e.Data); errors.AppendLine(e.Data); } };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        using var reg = ctx.Token.Register(() => { try { p.Kill(true); } catch { } });
        await p.WaitForExitAsync(ctx.Token);
        if (p.ExitCode != 0) throw new StudioException("It didn't work: " + (errors.Length > 0 ? errors.ToString().Trim().Split('\n').Last() : $"exit code {p.ExitCode}"));
        return "Done.";
    });

    private static readonly Regex SafeId = new(@"^@?[A-Za-z0-9._\-/]+$");

    public string Add(PackageChangeBody b)
    {
        if (!SafeId.IsMatch(b.Id) || b.Version is { Length: > 0 } v && !Regex.IsMatch(v, @"^[\w.\-+~^*<>= |]+$")) throw new StudioException("That isn't a valid package name or version.");
        if (IsNpm(b.Path))
        {
            var pj = PackageJson(b.Path);
            var dir = Path.GetDirectoryName(pj)!;
            var m = Manager(dir);
            var spec = b.Id + (string.IsNullOrEmpty(b.Version) ? "" : "@" + b.Version);
            var args = m switch
            {
                "npm" => new List<string> { "install", spec },
                "yarn" => ["add", spec],
                _ => ["add", spec],
            };
            if (b.Dev) args.Add(m == "npm" ? "--save-dev" : "-D");
            return Job(pj, $"Adding {b.Id}", Tool(m), args, dir);
        }
        var full = Path.GetFullPath(b.Path);
        var a = new List<string> { "add", full, "package", b.Id };
        if (!string.IsNullOrEmpty(b.Version)) { a.Add("--version"); a.Add(b.Version); }
        return Job(full, $"Adding {b.Id}", "dotnet", a, Path.GetDirectoryName(full)!);
    }

    public string Remove(PackageChangeBody b)
    {
        if (!SafeId.IsMatch(b.Id)) throw new StudioException("That isn't a valid package name.");
        if (IsNpm(b.Path))
        {
            var pj = PackageJson(b.Path);
            var dir = Path.GetDirectoryName(pj)!;
            var m = Manager(dir);
            return Job(pj, $"Removing {b.Id}", Tool(m), [m == "npm" ? "uninstall" : "remove", b.Id], dir);
        }
        var full = Path.GetFullPath(b.Path);
        return Job(full, $"Removing {b.Id}", "dotnet", ["remove", full, "package", b.Id], Path.GetDirectoryName(full)!);
    }

    private static string Tool(string manager) => OperatingSystem.IsWindows() ? manager + ".cmd" : manager;
}
