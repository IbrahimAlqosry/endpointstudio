using Stackwright.Server.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Stackwright.Server.Services.Frontend;

public record FrontendRoute(string Path, string? Component, string Kind, List<string> Guards, string File, int Line);
public record FrontendComponent(string Name, string Kind, string? Selector, string File, int Line);
public record FrontendApiCall(string Id, string Method, string Url, string Path, string? Base, string? Caller, string File, int Line,
    string Match, string? EndpointId, string? EndpointRoute, string? EndpointMethod);

public sealed class FrontendModel
{
    public required string PackageJson { get; init; }
    public required string Directory { get; init; }
    public required string Name { get; init; }
    public required string Framework { get; init; }          // Angular | React | Next.js | Vue
    public string? FrameworkVersion { get; set; }
    public int FrameworkMajor { get; set; }
    public string? BuildTool { get; set; }
    public required string PackageManager { get; init; }
    public bool Installed { get; set; }
    public Dictionary<string, string> Scripts { get; } = new();
    public Dictionary<string, string> Dependencies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? OutputDir { get; set; }
    public string? DevScript { get; set; }
    public bool TypeScript { get; set; }
    public List<FrontendRoute> Routes { get; } = [];
    public List<FrontendComponent> Components { get; } = [];
    public List<FrontendApiCall> ApiCalls { get; } = [];
    public List<string> Warnings { get; } = [];
    public int SourceFiles { get; set; }
    public Dictionary<string, string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase); // relative path -> text
    public bool NeedsLegacyOpenSsl { get; set; }
}

/// <summary>Reads Angular, React (Vite / Create React App), Next.js and Vue projects without running them.</summary>
public static class FrontendAnalyzer
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "dist", "build", "out", ".angular", ".next", ".nuxt", "coverage", ".git", ".vs", ".vscode", ".idea", "bin", "obj", "public", "assets", ".cache", ".turbo", "e2e", "cypress" };

    /// <summary>Which kind of frontend lives in a folder (null when it isn't one).</summary>
    public static string? Detect(string dir)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, "angular.json"))) return "Angular";
            var pkg = Path.Combine(dir, "package.json");
            if (!File.Exists(pkg)) return null;
            var deps = ReadDeps(pkg);
            if (deps.ContainsKey("@angular/core")) return "Angular";
            if (deps.ContainsKey("next")) return "Next.js";
            if (deps.ContainsKey("react")) return "React";
            if (deps.ContainsKey("vue")) return "Vue";
            return null;
        }
        catch { return null; }
    }

    private static Dictionary<string, string> ReadDeps(string packageJson)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = JsonNode.Parse(File.ReadAllText(packageJson), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        foreach (var section in new[] { "dependencies", "devDependencies" })
            if (root?[section] is JsonObject o)
                foreach (var (k, v) in o) map.TryAdd(k, v?.ToString() ?? "");
        return map;
    }

    public static string? InstalledVersion(string dir, string package)
    {
        try
        {
            var p = Path.Combine(dir, "node_modules", package.Replace('/', Path.DirectorySeparatorChar), "package.json");
            return File.Exists(p) ? JsonNode.Parse(File.ReadAllText(p))?["version"]?.ToString() : null;
        }
        catch { return null; }
    }

    public static FrontendModel Analyze(string path)
    {
        var dir = File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path))! : Path.GetFullPath(path);
        var pkgPath = Path.Combine(dir, "package.json");
        if (!File.Exists(pkgPath)) throw new StudioException("This folder doesn't contain a package.json, so it isn't a JavaScript project.");
        var framework = Detect(dir) ?? throw new StudioException("This doesn't look like an Angular, React, Next.js or Vue project.");
        JsonNode? pkg;
        try { pkg = JsonNode.Parse(File.ReadAllText(pkgPath), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }); }
        catch { throw new StudioException("package.json is damaged and can't be read."); }

        var model = new FrontendModel
        {
            PackageJson = pkgPath,
            Directory = dir,
            Name = pkg?["name"]?.ToString() is { Length: > 0 } n ? n : Path.GetFileName(dir),
            Framework = framework,
            PackageManager = File.Exists(Path.Combine(dir, "pnpm-lock.yaml")) ? "pnpm" : File.Exists(Path.Combine(dir, "yarn.lock")) ? "yarn" : "npm",
            Installed = Directory.Exists(Path.Combine(dir, "node_modules")),
        };
        foreach (var (k, v) in ReadDeps(pkgPath)) model.Dependencies[k] = v;
        if (pkg?["scripts"] is JsonObject scripts) foreach (var (k, v) in scripts) model.Scripts[k] = v?.ToString() ?? "";
        model.TypeScript = model.Dependencies.ContainsKey("typescript") || File.Exists(Path.Combine(dir, "tsconfig.json"));

        var corePkg = framework switch { "Angular" => "@angular/core", "Next.js" => "next", "Vue" => "vue", _ => "react" };
        model.FrameworkVersion = InstalledVersion(dir, corePkg) ?? CleanVersion(model.Dependencies.GetValueOrDefault(corePkg));
        model.FrameworkMajor = int.TryParse(model.FrameworkVersion?.Split('.')[0], out var mj) ? mj : 0;

        DescribeBuild(model);
        LoadSources(model);
        FindComponents(model);
        FindRoutes(model);
        FindApiCalls(model);
        if (!model.Installed) model.Warnings.Add("Packages aren't installed yet (no node_modules folder). Use “Install packages” before running or building.");
        return model;
    }

    private static string? CleanVersion(string? range) => range is null ? null : Regex.Match(range, @"\d+(\.\d+){0,2}").Value is { Length: > 0 } v ? v : null;

    private static void DescribeBuild(FrontendModel m)
    {
        var d = m.Dependencies;
        switch (m.Framework)
        {
            case "Angular":
            {
                m.BuildTool = "Angular CLI";
                m.DevScript = m.Scripts.ContainsKey("start") ? "start" : null;
                try
                {
                    var aj = JsonNode.Parse(File.ReadAllText(Path.Combine(m.Directory, "angular.json")), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    var projects = aj?["projects"] as JsonObject;
                    var name = aj?["defaultProject"]?.ToString() ?? projects?.FirstOrDefault(p => p.Value?["projectType"]?.ToString() == "application").Key ?? projects?.FirstOrDefault().Key;
                    var build = name is null ? null : projects?[name]?["architect"]?["build"];
                    var builder = build?["builder"]?.ToString() ?? "";
                    var outNode = build?["options"]?["outputPath"];
                    var outPath = outNode is JsonObject o ? o["base"]?.ToString() : outNode?.ToString();
                    var modern = builder.EndsWith(":application") || builder.Contains("@angular/build");
                    m.BuildTool = modern ? "Angular CLI (esbuild)" : "Angular CLI (webpack)";
                    if (outPath is not null)
                    {
                        // The esbuild "application" builder writes the site into a browser/ sub-folder (configurable)
                        var browser = outNode is JsonObject ob ? ob["browser"]?.ToString() : null;
                        m.OutputDir = modern ? Path.Combine(outPath, browser ?? "browser") : outPath;
                    }
                }
                catch { /* defaults below */ }
                m.OutputDir ??= Path.Combine("dist", m.Name);
                m.NeedsLegacyOpenSsl = m.FrameworkMajor is > 0 and < 13;
                break;
            }
            case "Next.js":
                m.BuildTool = "Next.js";
                m.DevScript = m.Scripts.ContainsKey("dev") ? "dev" : null;
                var cfg = new[] { "next.config.js", "next.config.mjs", "next.config.ts" }.Select(f => Path.Combine(m.Directory, f)).FirstOrDefault(File.Exists);
                m.OutputDir = cfg is not null && File.ReadAllText(cfg).Contains("'export'") || cfg is not null && File.ReadAllText(cfg).Contains("\"export\"") ? "out" : ".next";
                break;
            default:
                if (d.ContainsKey("vite"))
                {
                    m.BuildTool = "Vite";
                    m.DevScript = m.Scripts.ContainsKey("dev") ? "dev" : m.Scripts.ContainsKey("start") ? "start" : null;
                    var vc = new[] { "vite.config.ts", "vite.config.js", "vite.config.mts", "vite.config.mjs" }.Select(f => Path.Combine(m.Directory, f)).FirstOrDefault(File.Exists);
                    var outDir = vc is null ? null : Regex.Match(File.ReadAllText(vc), @"outDir\s*:\s*['""]([^'""]+)").Groups[1].Value;
                    m.OutputDir = string.IsNullOrEmpty(outDir) ? "dist" : outDir;
                }
                else if (d.ContainsKey("react-scripts"))
                {
                    m.BuildTool = "Create React App";
                    m.DevScript = m.Scripts.ContainsKey("start") ? "start" : null;
                    m.OutputDir = "build";
                    m.NeedsLegacyOpenSsl = int.TryParse(CleanVersion(d["react-scripts"])?.Split('.')[0], out var rs) && rs < 5;
                }
                else if (d.ContainsKey("@vue/cli-service"))
                {
                    m.BuildTool = "Vue CLI";
                    m.DevScript = m.Scripts.ContainsKey("serve") ? "serve" : null;
                    m.OutputDir = "dist";
                }
                else
                {
                    m.BuildTool = d.ContainsKey("webpack") ? "webpack" : null;
                    m.DevScript = new[] { "dev", "start", "serve" }.FirstOrDefault(m.Scripts.ContainsKey);
                    m.OutputDir = "dist";
                }
                break;
        }
    }

    private static void LoadSources(FrontendModel m)
    {
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ts", ".tsx", ".js", ".jsx", ".vue", ".mjs" };
        void Walk(DirectoryInfo d, int depth)
        {
            if (depth > 12 || m.Sources.Count > 4000) return;
            IEnumerable<FileInfo> files;
            try { files = d.EnumerateFiles().ToList(); } catch { return; }
            foreach (var f in files)
            {
                if (!exts.Contains(f.Extension)) continue;
                var n = f.Name;
                if (n.EndsWith(".d.ts") || n.Contains(".spec.") || n.Contains(".test.") || n.Contains(".stories.")) continue;
                if (depth == 0 && (n.Contains(".config.") || n is "karma.conf.js" or "protractor.conf.js")) continue;
                if (f.Length > 1_500_000) continue; // bundled vendor files
                try { m.Sources[Path.GetRelativePath(m.Directory, f.FullName).Replace('\\', '/')] = File.ReadAllText(f.FullName); } catch { /* unreadable */ }
            }
            foreach (var sub in d.EnumerateDirectories())
                if (!SkipDirs.Contains(sub.Name) && !sub.Name.StartsWith('.')) Walk(sub, depth + 1);
        }
        Walk(new DirectoryInfo(m.Directory), 0);
        m.SourceFiles = m.Sources.Count;
    }

    public static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }

    // ------------------------------------------------------------------ Components

    private static readonly Regex AngularDecorated = new(@"@(Component|Injectable|Directive|Pipe|NgModule)\s*\(", RegexOptions.Compiled);
    private static readonly Regex ReactFn = new(@"export\s+(?:default\s+)?(?:async\s+)?function\s+([A-Z]\w*)|(?:export\s+)?const\s+([A-Z]\w*)\s*(?::\s*[\w.<>\[\], |]+)?\s*=\s*(?:React\.)?(?:memo|forwardRef)?\(?\s*(?:async\s*)?(?:\([^)]*\)|\w+)\s*(?::\s*[\w.<>\[\]| ]+)?\s*=>", RegexOptions.Compiled);
    private static readonly Regex HookFn = new(@"export\s+(?:default\s+)?function\s+(use[A-Z]\w*)|export\s+const\s+(use[A-Z]\w*)\s*=", RegexOptions.Compiled);

    private static void FindComponents(FrontendModel m)
    {
        foreach (var (file, text) in m.Sources)
        {
            if (m.Framework == "Angular" && file.EndsWith(".ts"))
            {
                foreach (Match d in AngularDecorated.Matches(text))
                {
                    var open = d.Index + d.Length - 1;
                    var close = SourceScanner.MatchingClose(text, open);
                    if (close < 0) continue;
                    var args = text[(open + 1)..close];
                    var cls = Regex.Match(text[close..Math.Min(text.Length, close + 400)], @"class\s+(\w+)");
                    if (!cls.Success) continue;
                    var kind = d.Groups[1].Value switch { "Component" => "component", "Injectable" => "service", "Directive" => "directive", "Pipe" => "pipe", _ => "module" };
                    var selector = Regex.Match(args, @"(?:selector|name)\s*:\s*['""`]([^'""`]+)").Groups[1].Value;
                    m.Components.Add(new FrontendComponent(cls.Groups[1].Value, kind, selector.Length > 0 ? selector : null, file, LineOf(text, d.Index)));
                }
            }
            else if (file.EndsWith(".vue"))
            {
                m.Components.Add(new FrontendComponent(Path.GetFileNameWithoutExtension(file), "component", null, file, 1));
            }
            else if (file.EndsWith(".tsx") || file.EndsWith(".jsx") || (file.EndsWith(".js") && text.Contains("React")))
            {
                var clean = SourceScanner.StripComments(text);
                foreach (Match r in ReactFn.Matches(clean))
                {
                    var name = r.Groups[1].Success ? r.Groups[1].Value : r.Groups[2].Value;
                    if (m.Components.Any(c => c.Name == name && c.File == file)) continue;
                    m.Components.Add(new FrontendComponent(name, IsPageFile(file) ? "page" : "component", null, file, LineOf(clean, r.Index)));
                }
            }
            if (m.Framework != "Angular" && (file.EndsWith(".ts") || file.EndsWith(".tsx") || file.EndsWith(".js") || file.EndsWith(".jsx")))
                foreach (Match h in HookFn.Matches(text))
                    m.Components.Add(new FrontendComponent(h.Groups[1].Success ? h.Groups[1].Value : h.Groups[2].Value, "hook", null, file, LineOf(text, h.Index)));
        }
    }

    private static bool IsPageFile(string file) =>
        Regex.IsMatch(file, @"(^|/)(pages|views|screens|routes)/", RegexOptions.IgnoreCase) || Regex.IsMatch(file, @"(^|/)page\.(t|j)sx?$");

    // ------------------------------------------------------------------ Routes

    private static void FindRoutes(FrontendModel m)
    {
        if (m.Framework == "Next.js") NextRoutes(m);

        var routeObjects = new List<(string File, int Start, int End, string Path, string? Component, string Kind, List<string> Guards, string? LazyImport)>();
        foreach (var (file, raw) in m.Sources)
        {
            if (!Regex.IsMatch(raw, @"\bpath\s*:")) continue;
            // Only files that use a router library (avoids mistaking other objects with a "path" for routes)
            if (!Regex.IsMatch(raw, @"from\s+['""](@angular/router|react-router|react-router-dom|vue-router|@tanstack/react-router|@reach/router)['""]|RouterModule\.(forRoot|forChild)|provideRouter\s*\(")) continue;
            var text = SourceScanner.MaskStringsAndComments(raw, keepStrings: true);
            foreach (Match p in Regex.Matches(text, @"(?<![\w.])path\s*:\s*(['""`])([^'""`]*)\1"))
            {
                var open = SourceScanner.EnclosingOpen(text, p.Index, '{', '}');
                if (open < 0) continue;
                var close = SourceScanner.MatchingClose(text, open);
                if (close < 0) continue;
                var body = SourceScanner.TopLevelOnly(raw, open, close);
                string? component = null, lazy = null;
                var kind = "page";
                if (Regex.Match(body, @"\b(?:component|Component)\s*:\s*(\w+)") is { Success: true } c) component = c.Groups[1].Value;
                else if (Regex.Match(body, @"\belement\s*:\s*<\s*(\w+)") is { Success: true } e) component = e.Groups[1].Value;
                if (Regex.Match(body, @"\bloadComponent\s*:[^,}]*?import\(\s*['""]([^'""]+)['""]\s*\)(?:\s*\.then\(\s*\(?\s*\w+\s*\)?\s*=>\s*\w+\.(\w+))?") is { Success: true } lc)
                { component = lc.Groups[2].Success ? lc.Groups[2].Value : Path.GetFileNameWithoutExtension(lc.Groups[1].Value); kind = "lazy page"; }
                if (Regex.Match(body, @"\bloadChildren\s*:[^}]*?import\(\s*['""]([^'""]+)['""]\s*\)(?:\s*\.then\(\s*\(?\s*\w+\s*\)?\s*=>\s*\w+\.(\w+))?") is { Success: true } lch)
                { lazy = lch.Groups[1].Value; component = lch.Groups[2].Success ? lch.Groups[2].Value : null; kind = "section"; }
                else if (Regex.Match(body, @"\bloadChildren\s*:\s*['""]([^'""#]+)#?(\w*)") is { Success: true } old) // Angular <= 8 string syntax
                { lazy = old.Groups[1].Value; component = old.Groups[2].Value.Length > 0 ? old.Groups[2].Value : null; kind = "section"; }
                if (Regex.Match(body, @"\bredirectTo\s*:\s*['""]([^'""]*)") is { Success: true } rd) { component = "→ /" + rd.Groups[1].Value.TrimStart('/'); kind = "redirect"; }
                var guards = Regex.Matches(body, @"\bcan(?:Activate|Match|Load|ActivateChild)\s*:\s*\[([^\]]*)\]")
                    .SelectMany(g => g.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
                routeObjects.Add((file, open, close, p.Groups[2].Value, component, kind, guards, lazy));
            }
        }

        // Full paths: parent route objects (children arrays) and lazily loaded route files
        var prefixByDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var pass = 0; pass < 4; pass++)
        {
            foreach (var r in routeObjects.Where(r => r.LazyImport is not null))
            {
                var full = FullPath(r, routeObjects, prefixByDir);
                var target = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(m.Directory, Path.GetDirectoryName(r.File) ?? "", r.LazyImport!)))!;
                var rel = Path.GetRelativePath(m.Directory, target).Replace('\\', '/');
                prefixByDir[rel] = full;
            }
        }
        foreach (var r in routeObjects)
        {
            var full = FullPath(r, routeObjects, prefixByDir);
            if (m.Routes.Any(x => x.Path == full && x.File == r.File && x.Component == r.Component)) continue;
            m.Routes.Add(new FrontendRoute(full, r.Component, r.Kind, r.Guards, r.File, LineOf(m.Sources[r.File], r.Start)));
        }

        // React Router JSX: <Route path="..." element={<X />}>, nested
        foreach (var (file, text) in m.Sources.Where(s => (s.Key.EndsWith("x") || s.Key.EndsWith(".js")) && s.Value.Contains("<Route")))
        {
            var stack = new Stack<string>();
            var i = 0;
            while (i < text.Length)
            {
                var openTag = text.IndexOf("<Route", i, StringComparison.Ordinal);
                var closeTag = text.IndexOf("</Route>", i, StringComparison.Ordinal);
                if (openTag < 0 && closeTag < 0) break;
                if (closeTag >= 0 && (openTag < 0 || closeTag < openTag)) { if (stack.Count > 0) stack.Pop(); i = closeTag + 8; continue; }
                var after = openTag + 6;
                if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == 's')) { i = after; continue; } // <Routes>, <RouteX>
                var end = SourceScanner.TagEnd(text, after);
                if (end < 0) break;
                var tag = text[openTag..(end + 1)];
                var selfClosing = text[end - 1] == '/';
                var path = Regex.Match(tag, @"\bpath\s*=\s*(?:\{\s*)?['""`]([^'""`]*)").Groups[1].Value;
                var index = Regex.IsMatch(tag, @"\bindex\b");
                var comp = Regex.Match(tag, @"\b(?:element|component|Component)\s*=\s*\{\s*<?\s*(\w+)").Groups[1].Value;
                var parent = stack.Count > 0 ? stack.Peek() : "";
                var full = path.StartsWith('/') ? path : CombineRoute(parent, path);
                if (path.Length > 0 || index)
                    m.Routes.Add(new FrontendRoute(index && path.Length == 0 ? (parent.Length == 0 ? "/" : parent) : full, comp.Length > 0 ? comp : null, "page", [], file, LineOf(text, openTag)));
                if (!selfClosing) stack.Push(path.Length > 0 ? full : parent);
                i = end + 1;
            }
        }
    }

    private static string FullPath((string File, int Start, int End, string Path, string? Component, string Kind, List<string> Guards, string? LazyImport) r,
        List<(string File, int Start, int End, string Path, string? Component, string Kind, List<string> Guards, string? LazyImport)> all,
        Dictionary<string, string> prefixByDir)
    {
        var parts = new List<string> { r.Path };
        var current = r;
        var depth = 0;
        while (depth++ < 10)
        {
            var parent = all.Where(p => p.File == current.File && p.Start < current.Start && p.End > current.End)
                .OrderByDescending(p => p.Start).FirstOrDefault();
            if (parent.File is null) break;
            parts.Insert(0, parent.Path);
            current = parent;
        }
        var dir = (Path.GetDirectoryName(r.File) ?? "").Replace('\\', '/');
        var prefix = prefixByDir.TryGetValue(dir, out var px) ? px : "";
        var joined = parts.Aggregate(prefix, CombineRoute);
        return joined.Length == 0 ? "/" : joined;
    }

    private static string CombineRoute(string a, string b)
    {
        var x = a.Trim('/');
        var y = b.Trim('/');
        return "/" + string.Join("/", new[] { x, y }.Where(s => s.Length > 0));
    }

    private static void NextRoutes(FrontendModel m)
    {
        foreach (var file in m.Sources.Keys)
        {
            var f = Regex.Replace(file, "^src/", "");
            var app = Regex.Match(f, @"^app/(.*?)(?:^|/)?(page|route)\.(tsx|ts|jsx|js)$");
            if (app.Success)
            {
                var segs = app.Groups[1].Value.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Where(s => !(s.StartsWith('(') && s.EndsWith(')')) && !s.StartsWith('@'))
                    .Select(NextSegment);
                var route = "/" + string.Join("/", segs);
                var isApi = app.Groups[2].Value == "route";
                m.Routes.Add(new FrontendRoute(route, isApi ? "API route" : PascalFromFile(file), isApi ? "api" : "page", [], file, 1));
                continue;
            }
            var pages = Regex.Match(f, @"^pages/(.+)\.(tsx|ts|jsx|js)$");
            if (pages.Success)
            {
                var p = pages.Groups[1].Value;
                if (Regex.IsMatch(p, @"(^|/)_(app|document|error)$")) continue;
                var isApi = p.StartsWith("api/");
                var segs = p.Split('/').Select(NextSegment).ToList();
                if (segs[^1] == "index") segs.RemoveAt(segs.Count - 1);
                m.Routes.Add(new FrontendRoute("/" + string.Join("/", segs), isApi ? "API route" : PascalFromFile(file), isApi ? "api" : "page", [], file, 1));
            }
        }
    }

    private static string NextSegment(string s) =>
        s.StartsWith("[[...") ? "*" : s.StartsWith("[...") ? "*" : s.StartsWith('[') ? ":" + s.Trim('[', ']') : s;

    private static string PascalFromFile(string file) =>
        string.Concat(Regex.Split(Path.GetFileNameWithoutExtension(Path.GetDirectoryName(file) ?? file) ?? "Page", @"[^A-Za-z0-9]+")
            .Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w[1..])) is { Length: > 0 } s ? s + "Page" : "Page";

    // ------------------------------------------------------------------ API calls

    private static readonly Regex HttpClientCall = new(@"([\w$.]*?[hH]ttp\w*)\s*\.\s*(get|post|put|patch|delete|request|head)\s*(<(?:[^<>()]|<[^<>()]*>)*>)?\s*\(", RegexOptions.Compiled);
    private static readonly Regex FetchCall = new(@"(?<![\w.])fetch\s*\(", RegexOptions.Compiled);
    private static readonly Regex AxiosCreate = new(@"(?:const|let|var|this\.)\s*(\w+)\s*[:=][^=;]*?axios\.create\s*\(", RegexOptions.Compiled);

    private static void FindApiCalls(FrontendModel m)
    {
        var axiosNames = new HashSet<string> { "axios" };
        foreach (var t in m.Sources.Values) foreach (Match a in AxiosCreate.Matches(t)) axiosNames.Add(a.Groups[1].Value);
        var axiosCall = new Regex(@"(?<![\w.])(" + string.Join("|", axiosNames.Select(Regex.Escape)) + @")\s*\.\s*(get|post|put|patch|delete|head)\s*(<(?:[^<>()]|<[^<>()]*>)*>)?\s*\(", RegexOptions.Compiled);

        foreach (var (file, raw) in m.Sources)
        {
            var masked = SourceScanner.MaskStringsAndComments(raw, keepStrings: true);
            var codeOnly = SourceScanner.MaskStringsAndComments(raw, keepStrings: false);

            void Add(string method, int callIndex, int argsOpen, int urlArg)
            {
                var close = SourceScanner.MatchingClose(raw, argsOpen);
                if (close < 0) return;
                var args = SourceScanner.SplitTopLevel(raw[(argsOpen + 1)..close]);
                if (args.Count <= urlArg) return;
                var urlExpr = args[urlArg].Trim();
                if (Regex.IsMatch(urlExpr, @"^[A-Za-z_$][\w$]*$")) urlExpr = ResolveVariable(raw, urlExpr, callIndex) ?? urlExpr;
                var (url, path, baseExpr) = NormalizeUrl(urlExpr);
                if (method == "FETCH")
                {
                    var opts = args.Count > 1 ? args[1] : "";
                    method = Regex.Match(opts, @"method\s*:\s*['""`](\w+)") is { Success: true } mm ? mm.Groups[1].Value.ToUpperInvariant() : "GET";
                }
                var line = LineOf(raw, callIndex);
                var caller = EnclosingFunction(codeOnly, callIndex);
                var id = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{file}|{line}|{method}|{url}")))[..12];
                m.ApiCalls.Add(new FrontendApiCall(id, method, url, path, baseExpr, caller, file, line, "unknown", null, null, null));
            }

            foreach (Match c in HttpClientCall.Matches(masked))
            {
                if (codeOnly[c.Index] == ' ' && codeOnly.Substring(c.Index, c.Length).Trim().Length == 0) continue; // inside a string/comment
                var receiver = c.Groups[1].Value;
                if (receiver.EndsWith("Interceptor", StringComparison.OrdinalIgnoreCase) || receiver.Contains("HttpParams") || receiver.Contains("HttpHeaders")) continue;
                if (!IsCode(codeOnly, c.Index)) continue;
                var verb = c.Groups[2].Value.ToUpperInvariant();
                var open = c.Index + c.Length - 1;
                if (verb == "REQUEST")
                {
                    var close = SourceScanner.MatchingClose(raw, open);
                    if (close < 0) continue;
                    var args = SourceScanner.SplitTopLevel(raw[(open + 1)..close]);
                    var mm = args.Count > 0 ? Regex.Match(args[0], @"['""`](\w+)['""`]") : Match.Empty;
                    if (!mm.Success) continue;
                    Add(mm.Groups[1].Value.ToUpperInvariant(), c.Index, open, 1);
                }
                else Add(verb, c.Index, open, 0);
            }
            foreach (Match c in FetchCall.Matches(masked))
                if (IsCode(codeOnly, c.Index)) Add("FETCH", c.Index, c.Index + c.Length - 1, 0);
            foreach (Match c in axiosCall.Matches(masked))
                if (IsCode(codeOnly, c.Index)) Add(c.Groups[2].Value.ToUpperInvariant(), c.Index, c.Index + c.Length - 1, 0);
        }
    }

    private static bool IsCode(string codeOnly, int index) => index < codeOnly.Length && !char.IsWhiteSpace(codeOnly[index]);

    /// <summary>Finds `const url = …;` (or let/var/this.url =) declared before the call in the same file.</summary>
    private static string? ResolveVariable(string text, string name, int before)
    {
        var rx = new Regex(@"(?:(?:const|let|var)\s+" + Regex.Escape(name) + @"|this\." + Regex.Escape(name) + @")\s*(?::\s*[\w<>\[\]| ]+)?\s*=\s*([^;\n]+)");
        Match? best = null;
        foreach (Match mm in rx.Matches(text)) if (mm.Index < before) best = mm;
        return best?.Groups[1].Value.Trim();
    }

    /// <summary>Turns a URL expression into a readable URL, a path pattern (params as {}) and the base it starts from.</summary>
    public static (string Url, string Path, string? Base) NormalizeUrl(string expr)
    {
        var parts = SourceScanner.SplitTopLevel(expr, '+');
        var sb = new StringBuilder();
        string? baseExpr = null;
        for (var i = 0; i < parts.Count; i++)
        {
            var p = parts[i].Trim();
            if (p.Length == 0) continue;
            if ((p[0] is '\'' or '"') && p.Length >= 2 && p[^1] == p[0]) sb.Append(p[1..^1]);
            else if (p[0] == '`' && p.Length >= 2 && p[^1] == '`')
            {
                var inner = p[1..^1];
                var first = true;
                sb.Append(Regex.Replace(inner, @"\$\{([^}]*)\}", mm =>
                {
                    var leading = first && mm.Index == 0 && sb.Length == 0;
                    first = false;
                    if (leading) { baseExpr ??= mm.Groups[1].Value.Trim(); return ""; }
                    return "{" + LastIdentifier(mm.Groups[1].Value) + "}";
                }));
            }
            else if (i == 0 && sb.Length == 0) baseExpr = p;
            else sb.Append('{').Append(LastIdentifier(p)).Append('}');
        }
        var url = sb.ToString();
        var display = (baseExpr is null ? "" : "{" + LastIdentifier(baseExpr) + "}") + (url.StartsWith('/') || baseExpr is null ? "" : "/") + url;
        var path = url;
        var hostMatch = Regex.Match(path, @"^[a-z]+://[^/]+", RegexOptions.IgnoreCase);
        if (hostMatch.Success) { baseExpr ??= hostMatch.Value; path = path[hostMatch.Length..]; }
        var q = path.IndexOfAny(['?', '#']);
        if (q >= 0) path = path[..q];
        path = Regex.Replace(path, @"/{2,}", "/").Trim('/');
        return (display, path, baseExpr);
    }

    private static string LastIdentifier(string expr)
    {
        var ids = Regex.Matches(expr, @"[A-Za-z_$][\w$]*").Select(x => x.Value).Where(v => v is not ("this" or "encodeURIComponent" or "String" or "toString")).ToList();
        return ids.Count > 0 ? ids[^1] : "value";
    }

    private static string? EnclosingFunction(string code, int index)
    {
        var start = Math.Max(0, index - 4000);
        var window = code[start..index];
        Match? best = null;
        foreach (Match mm in Regex.Matches(window, @"(?:^|[\s;}])(?:public\s+|private\s+|protected\s+|async\s+|static\s+)*(?:function\s+)?([A-Za-z_$][\w$]*)\s*(?:<[^>]*>)?\s*\([^()]*\)\s*(?::\s*[^{=;]+)?\s*\{|(?:const|let)\s+([A-Za-z_$][\w$]*)\s*=\s*(?:async\s*)?\([^()]*\)\s*(?::\s*[^=]+)?=>", RegexOptions.Multiline))
            best = mm;
        if (best is null) return null;
        var name = best.Groups[1].Success ? best.Groups[1].Value : best.Groups[2].Value;
        return name is "if" or "for" or "while" or "switch" or "catch" or "function" or "subscribe" or "then" or "pipe" ? null : name;
    }

    // ------------------------------------------------------------------ Matching with a .NET API

    public static void MatchWithBackend(FrontendModel m, ProjectModel? backend)
    {
        if (backend is null) return;
        var endpoints = backend.Endpoints.Select(e => (Info: e.Info, Segs: Segments(e.Info.Route))).ToList();
        for (var i = 0; i < m.ApiCalls.Count; i++)
        {
            var call = m.ApiCalls[i];
            var segs = Segments(call.Path);
            // Files the app loads from itself (translations, config, images) aren't API calls
            if (Regex.IsMatch(call.Url, @"^\.?/?assets/|\.(json|svg|png|jpe?g|gif|txt|xml|csv|html?|md)$", RegexOptions.IgnoreCase) && !call.Url.Contains("api/", StringComparison.OrdinalIgnoreCase))
            { m.ApiCalls[i] = call with { Match = "asset" }; continue; }
            // A URL made only of variables can't be checked without running the app
            if (segs.Count == 0 || segs.All(s => s == "{}")) { m.ApiCalls[i] = call with { Match = "unknown" }; continue; }
            var candidates = endpoints.Where(e => SuffixMatch(e.Segs, segs)).OrderBy(e => e.Segs.Count - segs.Count).ToList();
            var exact = candidates.FirstOrDefault(e => e.Info.HttpMethod == call.Method || e.Info.HttpMethod == "ANY");
            if (exact.Info is not null)
                m.ApiCalls[i] = call with { Match = "matched", EndpointId = exact.Info.Id, EndpointRoute = exact.Info.Route, EndpointMethod = exact.Info.HttpMethod };
            else if (candidates.Count > 0)
                m.ApiCalls[i] = call with { Match = "method", EndpointId = candidates[0].Info.Id, EndpointRoute = candidates[0].Info.Route, EndpointMethod = candidates[0].Info.HttpMethod };
            else
                m.ApiCalls[i] = call with { Match = "missing" };
        }
    }

    private static List<string> Segments(string route) =>
        route.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Regex.IsMatch(s, @"^\{.*\}$|^:") || s.Contains('{') ? "{}" : s.ToLowerInvariant())
            .ToList();

    private static bool SuffixMatch(List<string> endpoint, List<string> call)
    {
        if (call.Count > endpoint.Count) return false;
        var offset = endpoint.Count - call.Count;
        for (var i = 0; i < call.Count; i++)
        {
            var e = endpoint[offset + i];
            var c = call[i];
            if (e == "{}" || c == "{}") continue;
            if (e != c) return false;
        }
        // Calls that are only a placeholder (e.g. "{id}") are too vague to match
        return call.Any(s => s != "{}");
    }
}
