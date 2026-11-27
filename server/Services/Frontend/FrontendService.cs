using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using QosriStudio.Server.Models;
using QosriStudio.Server.Services.Contracts;

namespace QosriStudio.Server.Services.Frontend;

public record ClientFile(string Path, bool Exists, bool Kept);
public record ClientPlan(string Folder, string Style, int Operations, int Types, List<ClientFile> Files, string Usage);

public record EndpointBrief(string Id, string Method, string Route, string Name);

public record FrontendDetails(
    string Kind,
    string Name,
    string Path,
    string Directory,
    string Framework,
    string? FrameworkVersion,
    int FrameworkMajor,
    string? BuildTool,
    string PackageManager,
    bool Installed,
    bool TypeScript,
    Dictionary<string, string> Scripts,
    string? OutputDir,
    string? DevScript,
    List<FrontendRoute> Routes,
    List<FrontendComponent> Components,
    List<FrontendApiCall> ApiCalls,
    List<string> Warnings,
    int SourceFiles,
    string? LinkedBackend,
    string? LinkedBackendName,
    List<EndpointBrief> UnusedEndpoints,
    List<ProjectFileEntry> SuggestedBackends,
    string? SupportNote,
    List<HistoryEntry> History);

/// <summary>Everything the studio does with Angular / React / Next.js / Vue projects.</summary>
public class FrontendService(HistoryService history, FileSystemService fs, JobService jobs)
{
    private static string LinksFile => System.IO.Path.Combine(AppStorage.Root, "links.json");
    private static readonly Lock LinkLock = new();

    public static string NormalizePath(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (Directory.Exists(full)) full = System.IO.Path.Combine(full, "package.json");
        else if (System.IO.Path.GetFileName(full).Equals("angular.json", StringComparison.OrdinalIgnoreCase)) full = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(full)!, "package.json");
        return full;
    }

    // ------------------------------------------------------------------ Linking to a .NET API

    private static Dictionary<string, string> Links()
    {
        try { return File.Exists(LinksFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LinksFile)) ?? [] : []; }
        catch { return []; }
    }

    public static string? LinkedBackend(string packageJson)
    {
        var links = Links();
        return links.TryGetValue(packageJson.ToLowerInvariant(), out var b) && File.Exists(b) ? b : null;
    }

    public void Link(string packageJson, string? backend)
    {
        lock (LinkLock)
        {
            var links = Links();
            var key = packageJson.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(backend)) links.Remove(key);
            else
            {
                if (!File.Exists(backend) || !backend.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) throw new StudioException("Pick the .NET API project (.csproj) this app talks to.");
                links[key] = System.IO.Path.GetFullPath(backend);
            }
            Directory.CreateDirectory(AppStorage.Root);
            File.WriteAllText(LinksFile, JsonSerializer.Serialize(links, AppStorage.Json));
        }
    }

    private List<ProjectFileEntry> SuggestBackends(string dir)
    {
        var found = new List<ProjectFileEntry>();
        var parent = Directory.GetParent(dir);
        foreach (var root in new[] { parent?.FullName, parent?.Parent?.FullName }.Where(r => r is not null))
        {
            try { found.AddRange(fs.FindProjects(root!, 4).Where(p => p.IsWeb)); } catch { /* unreadable */ }
            if (found.Count > 0) break;
        }
        return found.DistinctBy(p => p.Path.ToLowerInvariant()).Take(8).ToList();
    }

    // ------------------------------------------------------------------ Details

    public (FrontendModel Model, ProjectModel? Backend) Load(string path)
    {
        var pkg = NormalizePath(path);
        var model = FrontendAnalyzer.Analyze(pkg);
        ProjectModel? backend = null;
        if (LinkedBackend(pkg) is { } b)
        {
            try { backend = ProjectAnalyzer.Analyze(b); FrontendAnalyzer.MatchWithBackend(model, backend); }
            catch (StudioException ex) { model.Warnings.Add("The connected API project couldn't be read: " + ex.Message); }
        }
        return (model, backend);
    }

    public FrontendDetails Details(string path)
    {
        var (m, backend) = Load(path);
        var used = m.ApiCalls.Where(c => c.EndpointId is not null).Select(c => c.EndpointId!).ToHashSet();
        var unused = backend?.Endpoints.Where(e => !used.Contains(e.Info.Id) && e.Info.Route != "/")
            .Select(e => new EndpointBrief(e.Info.Id, e.Info.HttpMethod, e.Info.Route, e.Info.Name)).ToList() ?? [];
        return new FrontendDetails("frontend", m.Name, m.PackageJson, m.Directory, m.Framework, m.FrameworkVersion, m.FrameworkMajor, m.BuildTool,
            m.PackageManager, m.Installed, m.TypeScript, m.Scripts, m.OutputDir, m.DevScript,
            m.Routes.OrderBy(r => r.Path).ToList(), m.Components.OrderBy(c => c.Kind).ThenBy(c => c.Name).ToList(), m.ApiCalls, m.Warnings, m.SourceFiles,
            backend?.CsprojPath, backend?.Name, unused,
            backend is null ? SuggestBackends(m.Directory) : [], SupportNote(m), history.GetEntries(m.PackageJson));
    }

    // ------------------------------------------------------------------ Typed API client

    private (List<GeneratedFile> Files, string Folder, string Style, ApiContract Contract, string Usage) PlanClient(FrontendModel m, ProjectModel backend)
    {
        var contract = ApiContract.From(backend);
        if (contract.Operations.Count == 0) throw new StudioException("The connected API has no endpoints yet.");
        var dir = m.Directory;
        string folder;
        if (m.Framework == "Angular") folder = Directory.Exists(System.IO.Path.Combine(dir, "src", "app", "core")) ? "src/app/core/api" : "src/app/api";
        else if (m.Framework == "Next.js") folder = Directory.Exists(System.IO.Path.Combine(dir, "src")) ? "src/lib/api" : "lib/api";
        else folder = Directory.Exists(System.IO.Path.Combine(dir, "src")) ? "src/api" : "api";

        // Angular: use the API address from environment.ts when it has one
        string? envImport = null, envProp = null;
        var env = System.IO.Path.Combine(dir, "src", "environments", "environment.ts");
        if (m.Framework == "Angular" && File.Exists(env))
        {
            var match = Regex.Match(File.ReadAllText(env), @"\b(\w*(?:api|Api|API|base|Base)\w*(?:Url|URL|Uri|Address|Root)\w*|apiUrl|baseUrl|apiBase|serverUrl)\s*:");
            if (match.Success)
            {
                envProp = match.Groups[1].Value;
                var rel = System.IO.Path.GetRelativePath(System.IO.Path.Combine(dir, folder.Replace('/', System.IO.Path.DirectorySeparatorChar)), System.IO.Path.Combine(dir, "src", "environments", "environment")).Replace(System.IO.Path.DirectorySeparatorChar, '/');
                envImport = rel.StartsWith('.') ? rel : "./" + rel;
            }
        }
        var envExpr = m.Framework == "Next.js" ? "process.env.NEXT_PUBLIC_API_URL"
            : m.BuildTool == "Create React App" ? "process.env.REACT_APP_API_URL"
            : m.BuildTool == "Vite" || m.Framework == "Vue" ? "import.meta.env.VITE_API_URL"
            : "''";
        var files = TypeScriptClient.Generate(contract, m.Framework, m.TypeScript || m.Framework == "Angular", folder, envImport, envProp, backend.Name, envExpr);
        var style = m.Framework == "Angular" ? "Angular service (HttpClient, Observables)" : $"{(m.TypeScript ? "TypeScript" : "JavaScript")} client using fetch";
        var usage = m.Framework == "Angular"
            ? $"Inject ApiService and call, for example, api.{contract.Operations[0].Tag}.{contract.Operations[0].Name}(…).subscribe(…)." + (envProp is null ? " Provide API_BASE_URL in your app config." : $" The address comes from environment.{envProp}.")
            : $"import {{ api }} from '{(folder.StartsWith("src/") ? "@/" + folder[4..] : folder)}' (or a relative path), then await api.{contract.Operations[0].Tag}.{contract.Operations[0].Name}(…). Set {Regex.Match(envExpr, @"[A-Z_]+_API_URL|VITE_API_URL").Value} to the API address.";
        return (files, folder, style, contract, usage);
    }

    private (FrontendModel Model, ProjectModel Backend) LoadLinked(string path)
    {
        var (m, backend) = Load(path);
        if (backend is null) throw new StudioException("Connect this app to its .NET API project first.");
        if (m.Framework != "Angular" && !m.Sources.Keys.Any()) throw new StudioException("No source files were found in this app.");
        return (m, backend);
    }

    public ClientPlan PreviewClient(string path)
    {
        var (m, backend) = LoadLinked(path);
        var (files, folder, style, contract, usage) = PlanClient(m, backend);
        return new ClientPlan(folder, style, contract.Operations.Count, contract.Types.Count,
            files.Select(f => { var exists = File.Exists(System.IO.Path.Combine(m.Directory, f.RelativePath)); return new ClientFile(f.RelativePath, exists, exists && f.KeepIfExists); }).ToList(), usage);
    }

    public string GenerateClient(string path)
    {
        var (m, backend) = LoadLinked(path);
        var (files, folder, _, contract, _) = PlanClient(m, backend);
        var changes = history.Begin(m.PackageJson, $"Generated the API client in {folder}");
        var written = 0;
        foreach (var f in files)
        {
            var full = System.IO.Path.Combine(m.Directory, f.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (f.KeepIfExists && File.Exists(full)) continue;
            changes.Write(full, f.Content);
            written++;
        }
        changes.Commit();
        return $"Wrote {written} file{(written == 1 ? "" : "s")} to {folder}: {contract.Operations.Count} endpoints and {contract.Types.Count} types.";
    }

    public static string? SupportNote(FrontendModel m) => m.Framework switch
    {
        // Angular majors get ~18 months of support; as of late 2026, 21 and newer are supported.
        "Angular" when m.FrameworkMajor is > 0 and < 21 => $"Angular {m.FrameworkMajor} no longer gets security fixes. Upgrading to a recent Angular version is recommended.",
        "React" when m.FrameworkMajor is > 0 and < 18 => $"React {m.FrameworkMajor} is several versions behind. React 18 and 19 add important improvements.",
        "Vue" when m.FrameworkMajor == 2 => "Vue 2 reached end of life in December 2023 and no longer gets security fixes.",
        _ => null,
    };

    // ------------------------------------------------------------------ Settings (environment files)

    private static readonly Regex SecretRx = new("password|pwd|secret|token|apikey|api_key|privatekey|private_key|clientsecret", RegexOptions.IgnoreCase);
    private static readonly Regex EnvProp = new(@"(?<key>[A-Za-z_$][\w$]*|'[^']+'|""[^""]+"")\s*:\s*(?<val>'(?:[^'\\\n]|\\.)*'|""(?:[^""\\\n]|\\.)*""|`[^`]*`|true|false|null|-?\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private static IEnumerable<string> EnvFiles(FrontendModel m)
    {
        if (m.Framework == "Angular")
        {
            var dir = System.IO.Path.Combine(m.Directory, "src", "environments");
            return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "environment*.ts").OrderBy(f => f.Length) : [];
        }
        return Directory.EnumerateFiles(m.Directory, ".env*").Where(f => !f.EndsWith(".example") && !f.EndsWith(".sample")).OrderBy(f => f.Length);
    }

    public SettingsInfo Settings(string path)
    {
        var m = FrontendAnalyzer.Analyze(NormalizePath(path));
        var files = new List<SettingsFile>();
        foreach (var f in EnvFiles(m))
        {
            var name = System.IO.Path.GetFileName(f);
            var text = File.ReadAllText(f);
            var env = m.Framework == "Angular"
                ? Regex.Match(name, @"^environment\.(.+)\.ts$").Groups[1].Value
                : Regex.Match(name, @"^\.env\.(.+)$").Groups[1].Value;
            var entries = m.Framework == "Angular" ? ParseTsEnvironment(text) : ParseDotEnv(text);
            files.Add(new SettingsFile(name, f, m.Framework == "Angular" ? "ts" : "env", env.Length > 0 ? env : null, false, entries));
        }
        return new SettingsInfo(files, files.Where(f => f.Environment is not null).Select(f => f.Environment!).ToList(), []);
    }

    private static (int Open, int Close)? EnvironmentObject(string text)
    {
        var m = Regex.Match(text, @"\benvironment\b\s*(?::\s*[\w.<>]+\s*)?=\s*\{");
        if (!m.Success) m = Regex.Match(text, @"export\s+default\s*\{");
        if (!m.Success) return null;
        var open = m.Index + m.Length - 1;
        var close = SourceScanner.MatchingClose(text, open);
        return close < 0 ? null : (open, close);
    }

    private static List<SettingEntry> ParseTsEnvironment(string text)
    {
        var list = new List<SettingEntry>();
        if (EnvironmentObject(text) is not { } span) return list;
        var body = SourceScanner.TopLevelOnly(text, span.Open, span.Close);
        foreach (Match p in EnvProp.Matches(body))
        {
            var key = p.Groups["key"].Value.Trim('\'', '"');
            var raw = p.Groups["val"].Value;
            var kind = raw is "true" or "false" ? "bool" : raw == "null" ? "null" : char.IsDigit(raw[0]) || raw[0] == '-' ? "number" : "string";
            var value = kind == "string" ? Regex.Unescape(raw[1..^1]) : raw;
            list.Add(new SettingEntry(key, value, kind, SecretRx.IsMatch(key)));
        }
        foreach (Match s in Regex.Matches(body, @"\.\.\.\s*([\w.]+)"))
            list.Add(new SettingEntry("…" + s.Groups[1].Value, "Includes the settings from " + s.Groups[1].Value, "error", false));
        return list;
    }

    private static List<SettingEntry> ParseDotEnv(string text)
    {
        var list = new List<SettingEntry>();
        foreach (var line in text.Split('\n'))
        {
            var m = Regex.Match(line.TrimEnd('\r'), @"^\s*(?:export\s+)?([A-Za-z_][\w.\-]*)\s*=\s*(.*)$");
            if (!m.Success) continue;
            var v = m.Groups[2].Value.Trim();
            if (v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\'')) v = v[1..^1];
            var kind = v is "true" or "false" ? "bool" : Regex.IsMatch(v, @"^-?\d+(\.\d+)?$") ? "number" : "string";
            list.Add(new SettingEntry(m.Groups[1].Value, v, kind, SecretRx.IsMatch(m.Groups[1].Value)));
        }
        return list;
    }

    public string SaveSettings(string path, SaveSettingsRequest req)
    {
        var pkg = NormalizePath(path);
        var m = FrontendAnalyzer.Analyze(pkg);
        var file = EnvFiles(m).FirstOrDefault(f => System.IO.Path.GetFileName(f).Equals(req.File, StringComparison.OrdinalIgnoreCase))
                   ?? throw new StudioException("That settings file doesn't exist anymore.");
        var changes = history.Begin(pkg, $"Changed settings in {req.File}");
        var text = File.ReadAllText(file);
        var updated = m.Framework == "Angular" ? ApplyTs(text, req.Changes) : ApplyDotEnv(text, req.Changes);
        changes.Write(file, updated);
        changes.Commit();
        return $"Saved {req.Changes.Count} change{(req.Changes.Count == 1 ? "" : "s")} to {req.File}.";
    }

    public string AddEnvironment(string path, string name)
    {
        if (!Regex.IsMatch(name ?? "", @"^[A-Za-z][A-Za-z0-9]*$")) throw new StudioException("Use letters and numbers for the environment name, for example staging.");
        var pkg = NormalizePath(path);
        var m = FrontendAnalyzer.Analyze(pkg);
        var changes = history.Begin(pkg, $"Added the {name} environment");
        string target;
        if (m.Framework == "Angular")
        {
            var dir = System.IO.Path.Combine(m.Directory, "src", "environments");
            var main = System.IO.Path.Combine(dir, "environment.ts");
            target = System.IO.Path.Combine(dir, $"environment.{name.ToLowerInvariant()}.ts");
            if (File.Exists(target)) throw new StudioException("That environment already exists.");
            changes.Write(target, File.Exists(main) ? File.ReadAllText(main) : "export const environment = {\r\n  production: true\r\n};\r\n");
        }
        else
        {
            target = System.IO.Path.Combine(m.Directory, $".env.{name.ToLowerInvariant()}");
            if (File.Exists(target)) throw new StudioException("That environment already exists.");
            changes.Write(target, "");
        }
        changes.Commit();
        return m.Framework == "Angular"
            ? $"Added {System.IO.Path.GetFileName(target)} as a copy of the main settings. To build with it, add a matching configuration in angular.json (fileReplacements)."
            : $"Added {System.IO.Path.GetFileName(target)}. It's used when the app runs or builds in “{name.ToLowerInvariant()}” mode.";
    }

    private static string TsLiteral(SettingChange c) => c.Kind switch
    {
        "bool" => c.Value == "true" ? "true" : "false",
        "number" => decimal.TryParse(c.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _) ? c.Value! : throw new StudioException($"“{c.Key}” must be a number."),
        "null" => "null",
        _ => "'" + (c.Value ?? "").Replace("\\", "\\\\").Replace("'", "\\'") + "'",
    };

    private static string ApplyTs(string text, List<SettingChange> changes)
    {
        foreach (var c in changes)
        {
            if (c.Key.StartsWith('…')) continue;
            if (!Regex.IsMatch(c.Key, @"^[A-Za-z_$][\w$]*$")) throw new StudioException($"“{c.Key}” can't be used as a setting name here (letters and numbers only).");
            var span = EnvironmentObject(text) ?? throw new StudioException("The environment settings object wasn't found in this file.");
            var body = SourceScanner.TopLevelOnly(text, span.Open, span.Close);
            var prop = new Regex(@"(?<![\w$])(?:" + Regex.Escape(c.Key) + @"|'" + Regex.Escape(c.Key) + @"'|""" + Regex.Escape(c.Key) + @""")\s*:\s*(?<val>'(?:[^'\\\n]|\\.)*'|""(?:[^""\\\n]|\\.)*""|`[^`]*`|true|false|null|-?\d+(?:\.\d+)?)").Match(body);
            var offset = span.Open + 1;
            if (prop.Success)
            {
                if (c.Remove)
                {
                    var start = offset + prop.Index;
                    var end = offset + prop.Index + prop.Length;
                    while (end < text.Length && text[end] is ' ' or '\t') end++;
                    if (end < text.Length && text[end] == ',') end++;
                    var ls = start; while (ls > 0 && text[ls - 1] is ' ' or '\t') ls--;
                    if (ls > 0 && text[ls - 1] == '\n' && end < text.Length && text[end] is '\r' or '\n') { start = ls; while (end < text.Length && text[end] is '\r' or '\n') { end++; if (text[end - 1] == '\n') break; } }
                    text = text[..start] + text[end..];
                }
                else
                {
                    var g = prop.Groups["val"];
                    text = text[..(offset + g.Index)] + TsLiteral(c) + text[(offset + g.Index + g.Length)..];
                }
            }
            else if (!c.Remove)
            {
                // Add before the closing brace, matching the indentation of the other settings
                var close = span.Close;
                var before = text[..close].TrimEnd();
                var needsComma = !before.EndsWith('{') && !before.EndsWith(',');
                var indent = Regex.Match(text[(span.Open + 1)..close], @"\n([ \t]+)\S").Groups[1].Value;
                if (indent.Length == 0) indent = "  ";
                var nl = text.Contains("\r\n") ? "\r\n" : "\n";
                text = before + (needsComma ? "," : "") + nl + indent + c.Key + ": " + TsLiteral(c) + nl + text[close..];
            }
        }
        return text;
    }

    private static string ApplyDotEnv(string text, List<SettingChange> changes)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        foreach (var c in changes)
        {
            if (!Regex.IsMatch(c.Key, @"^[A-Za-z_][\w.\-]*$")) throw new StudioException($"“{c.Key}” can't be used as a setting name (letters, numbers and _ only).");
            var idx = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*(?:export\s+)?" + Regex.Escape(c.Key) + @"\s*="));
            var value = c.Value ?? "";
            if (value.Contains(' ') || value.Contains('#')) value = "\"" + value.Replace("\"", "\\\"") + "\"";
            if (c.Remove) { if (idx >= 0) lines.RemoveAt(idx); }
            else if (idx >= 0) lines[idx] = $"{c.Key}={value}";
            else
            {
                if (lines.Count > 0 && lines[^1].Length == 0) lines.Insert(lines.Count - 1, $"{c.Key}={value}");
                else lines.Add($"{c.Key}={value}");
            }
        }
        return string.Join(nl, lines);
    }

    // ------------------------------------------------------------------ Install / build / run

    public static string PackageManagerPath(string pm) =>
        DeployService.FindOnPath(pm) ?? throw new StudioException(pm == "npm"
            ? "Node.js isn't installed. Install it from nodejs.org, then try again."
            : $"{pm} isn't installed. Install it (npm install -g {pm}) or delete its lock file to use npm.");

    public static Dictionary<string, string> NodeEnv(FrontendModel m)
    {
        var env = new Dictionary<string, string>
        {
            ["NG_CLI_ANALYTICS"] = "false",   // Angular CLI would otherwise ask a question and wait
            ["BROWSER"] = "none",            // Create React App would open a browser
            ["CI"] = "false",                // CRA treats warnings as errors when CI=true
            ["FORCE_COLOR"] = "0",
            ["NO_COLOR"] = "1",
        };
        if (m.NeedsLegacyOpenSsl) env["NODE_OPTIONS"] = "--openssl-legacy-provider"; // webpack 4 on Node 17+
        return env;
    }

    public string Install(string path)
    {
        var m = FrontendAnalyzer.Analyze(NormalizePath(path));
        var pm = PackageManagerPath(m.PackageManager);
        return jobs.Start(m.PackageJson, "install", $"Install packages for {m.Name}", async ctx =>
        {
            var args = m.PackageManager == "npm" ? new List<string> { "install", "--no-audit", "--no-fund" } : ["install"];
            if (m.PackageManager == "npm" && m.FrameworkMajor is > 0 and < 13 && m.Framework == "Angular") args.Add("--legacy-peer-deps");
            if (await JobService.RunAsync(ctx, pm, args, m.Directory, env: NodeEnv(m)) != 0)
                throw new StudioException("Installing packages failed. Old projects sometimes need an older Node.js version; the log shows the reason.");
            return "Packages installed.";
        });
    }

    public string Build(string path, string? configuration = null)
    {
        var m = FrontendAnalyzer.Analyze(NormalizePath(path));
        return jobs.Start(m.PackageJson, "build", $"Build {m.Name}", async ctx =>
        {
            var output = await BuildAsync(ctx, m, configuration);
            return $"Built successfully. The site is in {output}.";
        });
    }

    /// <summary>Installs packages when needed, runs the build script and returns the output folder.</summary>
    public static async Task<string> BuildAsync(JobService.Context ctx, FrontendModel m, string? configuration = null)
    {
        var pm = PackageManagerPath(m.PackageManager);
        if (!m.Installed)
        {
            ctx.Log("Installing packages first…");
            var args = m.PackageManager == "npm" ? new List<string> { File.Exists(System.IO.Path.Combine(m.Directory, "package-lock.json")) ? "ci" : "install", "--no-audit", "--no-fund" } : ["install"];
            if (m.PackageManager == "npm" && m.Framework == "Angular" && m.FrameworkMajor is > 0 and < 13) args.Add("--legacy-peer-deps");
            if (await JobService.RunAsync(ctx, pm, args, m.Directory, env: NodeEnv(m)) != 0) throw new StudioException("Installing packages failed. See the log.");
        }
        if (!m.Scripts.ContainsKey("build")) throw new StudioException("package.json has no “build” script.");
        var buildArgs = new List<string> { "run", "build" };
        if (m.Framework == "Angular" && !string.IsNullOrWhiteSpace(configuration)) buildArgs.AddRange(["--", "--configuration", configuration!]);
        if (await JobService.RunAsync(ctx, pm, buildArgs, m.Directory, env: NodeEnv(m)) != 0)
            throw new StudioException("The build failed. The log shows the errors.");
        var output = System.IO.Path.GetFullPath(System.IO.Path.Combine(m.Directory, m.OutputDir ?? "dist"));
        if (!Directory.Exists(output))
        {
            // Older Angular versions write to dist/<name> without browser/
            var alt = System.IO.Path.GetDirectoryName(output);
            if (alt is not null && Directory.Exists(alt) && File.Exists(System.IO.Path.Combine(alt, "index.html"))) output = alt;
            else throw new StudioException($"The build finished but its output folder ({m.OutputDir}) wasn't found.");
        }
        ctx.Log($"Output: {output}");
        return output;
    }

    // Dev server
    private sealed class DevServer
    {
        public required Process Process { get; init; }
        public string? Url { get; set; }
        public string State { get; set; } = "starting";
        public ConcurrentQueue<string> Log { get; } = new();
        public void Append(string l) { Log.Enqueue(l); while (Log.Count > 400) Log.TryDequeue(out _); }
    }
    private readonly ConcurrentDictionary<string, DevServer> _servers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex UrlRx = new(@"https?://(?:localhost|127\.0\.0\.1|\[::1?\]|0\.0\.0\.0)(?::\d+)?/?\S*", RegexOptions.Compiled);
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    /// <summary>Every front-end dev server this app has started (for the Running view).</summary>
    public List<string> RunningApps() => _servers.Where(kv => !kv.Value.Process.HasExited || kv.Value.State == "failed").Select(kv => kv.Key).ToList();

    public RunStatus DevStatus(string path)
    {
        var key = NormalizePath(path);
        if (!_servers.TryGetValue(key, out var s)) return new RunStatus(false, null, "stopped", []);
        if (s.Process.HasExited && s.State is "starting" or "running") s.State = s.State == "starting" ? "failed" : "stopped";
        return new RunStatus(!s.Process.HasExited, s.Url, s.State, s.Log.ToList());
    }

    public RunStatus StartDev(string path)
    {
        var key = NormalizePath(path);
        if (_servers.TryGetValue(key, out var existing) && !existing.Process.HasExited) return DevStatus(key);
        var m = FrontendAnalyzer.Analyze(key);
        if (!m.Installed) throw new StudioException("Install the packages first (button “Install packages”).");
        var script = m.DevScript ?? throw new StudioException("package.json has no start/dev script to run the app.");
        var pm = PackageManagerPath(m.PackageManager);
        var port = FreePort();
        var args = new List<string> { "run", script };
        var env = NodeEnv(m);
        var command = m.Scripts.GetValueOrDefault(script) ?? "";
        if (command.Contains("ng serve") || command.Contains("vite") || command.Contains("vue-cli-service")) args.AddRange(["--", "--port", port.ToString()]);
        else if (command.Contains("next")) args.AddRange(["--", "-p", port.ToString()]);
        else env["PORT"] = port.ToString(); // Create React App and most custom servers
        if (m.PackageManager != "npm") args.RemoveAll(a => a == "--");

        var psi = new ProcessStartInfo(pm) { WorkingDirectory = m.Directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var server = new DevServer { Process = proc };
        server.Append($"Starting {m.Name} ({m.BuildTool ?? m.Framework})…");
        void OnLine(string? raw)
        {
            if (raw is null) return;
            var line = Ansi.Replace(raw, "");
            server.Append(line);
            if (server.Url is null && UrlRx.Match(line) is { Success: true } u && !line.Contains("network", StringComparison.OrdinalIgnoreCase))
            {
                server.Url = u.Value.TrimEnd('/', '.', ',').Replace("0.0.0.0", "localhost").Replace("[::1]", "localhost").Replace("[::]", "localhost");
                server.State = "running";
            }
            if (server.State == "starting" && Regex.IsMatch(line, @"compiled successfully|ready in|Local:|started server", RegexOptions.IgnoreCase))
            {
                server.Url ??= $"http://localhost:{port}";
                server.State = "running";
            }
        }
        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
        proc.Exited += (_, _) =>
        {
            server.Append(server.State == "running" || server.State == "stopping" ? "The app stopped." : "The app couldn't start. See the messages above.");
            server.State = server.State is "running" or "stopping" ? "stopped" : "failed";
        };
        proc.Start();
        proc.StandardInput.Close();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _servers[key] = server;
        return DevStatus(key);
    }

    public RunStatus StopDev(string path)
    {
        var key = NormalizePath(path);
        if (_servers.TryGetValue(key, out var s) && !s.Process.HasExited)
        {
            s.State = "stopping";
            try { s.Process.Kill(entireProcessTree: true); s.Process.WaitForExit(5000); } catch { /* gone */ }
            s.State = "stopped";
        }
        return DevStatus(key);
    }

    public void StopAll()
    {
        foreach (var s in _servers.Values) try { if (!s.Process.HasExited) s.Process.Kill(true); } catch { /* ignore */ }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ------------------------------------------------------------------ Health

    public HealthReport Health(string path)
    {
        var (m, backend) = Load(path);
        var findings = new List<HealthFinding>();
        var settings = Settings(m.PackageJson);

        if (backend is not null)
        {
            var missing = m.ApiCalls.Where(c => c.Match == "missing").ToList();
            if (missing.Count > 0)
                findings.Add(new("high", "Reliability", $"{missing.Count} API call{S(missing.Count)} don't match any endpoint in {backend.Name}",
                    "These calls will fail with “404 Not Found” unless the address is built differently at run time. Check the spelling and path, or add the endpoint to the API.",
                    missing.Take(25).Select(c => $"{c.Method} {c.Url}  ({c.File}:{c.Line})").ToList()));
            var wrong = m.ApiCalls.Where(c => c.Match == "method").ToList();
            if (wrong.Count > 0)
                findings.Add(new("medium", "Reliability", $"{wrong.Count} API call{S(wrong.Count)} use a different method than the endpoint",
                    "The address exists but the API expects another HTTP method, so the call returns “405 Method Not Allowed”.",
                    wrong.Take(25).Select(c => $"{c.Method} {c.Url} → API expects {c.EndpointMethod} {c.EndpointRoute}").ToList()));
        }
        else if (m.ApiCalls.Count > 0)
            findings.Add(new("info", "Reliability", "Not connected to an API project",
                "Connect this app to its .NET API to check that every call matches a real endpoint.", []));

        var secrets = settings.Files.SelectMany(f => f.Entries.Where(e => e.Secret).Select(e => $"{f.Name}: {e.Key}")).ToList();
        if (secrets.Count > 0)
            findings.Add(new("high", "Security", "Secrets in the app's settings are visible to every visitor",
                "Everything in frontend settings is shipped to the browser, so anyone can read it. Keep passwords, private keys and secret tokens on the server (in the API) instead.",
                secrets));

        var prodFiles = settings.Files.Where(f => f.Environment is { } e && e.StartsWith("prod", StringComparison.OrdinalIgnoreCase)).ToList();
        var prodLocal = prodFiles.SelectMany(f => f.Entries.Where(e => e.Value is { } v && Regex.IsMatch(v, @"localhost|127\.0\.0\.1", RegexOptions.IgnoreCase)).Select(e => $"{f.Name}: {e.Key} = {e.Value}")).ToList();
        if (prodLocal.Count > 0)
            findings.Add(new("medium", "Reliability", "Production settings point to localhost",
                "A deployed site can't reach “localhost” on your computer. Use the real API address in the production settings.", prodLocal));
        var prodHttp = prodFiles.SelectMany(f => f.Entries.Where(e => e.Value is { } v && v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(v, "localhost|127\\.0\\.0\\.1")).Select(e => $"{f.Name}: {e.Key} = {e.Value}")).ToList();
        if (prodHttp.Count > 0)
            findings.Add(new("medium", "Security", "Production settings use unencrypted http://",
                "Browsers block calls from an https site to http addresses, and data travels unencrypted. Use https:// addresses in production.", prodHttp));

        var hardcoded = m.ApiCalls.Where(c => c.Base is { } b && Regex.IsMatch(b, "^https?://", RegexOptions.IgnoreCase)).ToList();
        if (hardcoded.Count > 0)
            findings.Add(new("low", "Maintenance", $"{hardcoded.Count} API call{S(hardcoded.Count)} have the server address written into the code",
                "Put the API address in the environment settings so the same build can target test and production servers.",
                hardcoded.Take(15).Select(c => $"{c.Url}  ({c.File}:{c.Line})").ToList()));

        if (SupportNote(m) is { } note) findings.Add(new("medium", "Maintenance", $"{m.Framework} {m.FrameworkMajor} is out of date", note, []));
        if (!File.Exists(System.IO.Path.Combine(m.Directory, "package-lock.json")) && !File.Exists(System.IO.Path.Combine(m.Directory, "yarn.lock")) && !File.Exists(System.IO.Path.Combine(m.Directory, "pnpm-lock.yaml")))
            findings.Add(new("low", "Reliability", "No lock file", "Without package-lock.json, every install can pick different package versions, so builds may change unexpectedly. Run “Install packages” once and keep the lock file.", []));
        if (m.NeedsLegacyOpenSsl)
            findings.Add(new("info", "Maintenance", "Built with older tools", "This project's build tools need a compatibility switch on current Node.js versions. QosriStudio adds it automatically when running and building.", []));

        var penalty = findings.Sum(f => f.Severity switch { "high" => 20, "medium" => 10, "low" => 4, _ => 0 });
        var score = Math.Clamp(100 - penalty, 0, 100);
        var order = new[] { "high", "medium", "low", "info" };
        return new HealthReport(score, score >= 90 ? "Excellent" : score >= 75 ? "Good" : score >= 55 ? "Needs attention" : "At risk",
            findings.OrderBy(f => Array.IndexOf(order, f.Severity)).ToList(), 8);
    }

    private static string S(int n) => n == 1 ? "" : "s";
}
