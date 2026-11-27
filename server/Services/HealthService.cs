using System.Text.RegularExpressions;
using QosriStudio.Server.Models;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace QosriStudio.Server.Services;

public record HealthFinding(string Severity, string Category, string Title, string Detail, List<string> Items, List<string>? EndpointIds = null);
public record HealthReport(int Score, string Grade, List<HealthFinding> Findings, int Checks);

/// <summary>Looks for common problems in an API project: security, reliability, documentation and maintenance.</summary>
public static class HealthService
{
    private static readonly string[] SecretFilePatterns = ["*.pfx", "*.p12", "*.pem", "*.key", "*key*.txt", "*secret*.txt", "*secret*.json", "*.jks"];

    public static HealthReport Check(ProjectModel m, ProjectCapabilities caps)
    {
        var findings = new List<HealthFinding>();
        var eps = m.Endpoints.Select(e => e.Info).ToList();
        const int checks = 10;

        // 1. Duplicate routes
        var dupes = eps.GroupBy(e => (e.HttpMethod, Shape(e.Route))).Where(g => g.Count() > 1).ToList();
        if (dupes.Count > 0)
            findings.Add(new("high", "Reliability", $"{dupes.Count} route{Plural(dupes.Count)} defined more than once",
                "Two endpoints answer the same method and path, so callers can't predict which one runs (ASP.NET Core usually fails the request with an \"ambiguous match\" error).",
                dupes.Select(g => $"{g.Key.HttpMethod} {g.First().Route} ({string.Join(", ", g.Select(e => e.Name))})").ToList(),
                dupes.SelectMany(g => g.Select(e => e.Id)).ToList()));

        // 2. Open endpoints in an API that otherwise uses sign-in
        var open = eps.Where(e => !e.RequiresAuth).ToList();
        if (eps.Count > 0 && open.Count > 0 && open.Count < eps.Count)
            findings.Add(new("medium", "Security", $"{open.Count} endpoint{Plural(open.Count)} can be called without signing in",
                "Most of this API requires sign-in, but these endpoints don't. Check that each one is meant to be public (login and health checks usually are).",
                open.Take(25).Select(e => $"{e.HttpMethod} {e.Route}").ToList(), open.Select(e => e.Id).ToList()));
        else if (eps.Count > 0 && open.Count == eps.Count)
            findings.Add(new("info", "Security", "No endpoint requires sign-in",
                "Every endpoint is public. That's fine for internal or demo APIs; otherwise add sign-in to the endpoints that change or expose data.", []));

        // 3. Key / certificate files inside the project
        var secretFiles = SecretFilePatterns
            .SelectMany(p => SafeEnumerate(m.Directory, p))
            .Where(f => !Regex.IsMatch(f, @"[\\/](bin|obj|node_modules)[\\/]", RegexOptions.IgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (secretFiles.Count > 0)
            findings.Add(new("high", "Security", $"{secretFiles.Count} key or certificate file{Plural(secretFiles.Count)} inside the project folder",
                "Private keys and certificates stored with the source code are easy to leak through source control, backups or deployments. Keep them in a secure store (for example Azure Key Vault, Windows certificate store or environment-specific secrets) and add them to .gitignore.",
                secretFiles.Select(f => Path.GetRelativePath(m.Directory, f)).ToList()));

        // 4. Passwords in configuration files
        var configHits = new List<string>();
        foreach (var cfg in SafeEnumerate(m.Directory, "appsettings*.json").Concat(SafeEnumerate(m.Directory, "web.config")).Concat(SafeEnumerate(m.Directory, "*.config")))
        {
            if (Regex.IsMatch(cfg, @"[\\/](bin|obj)[\\/]", RegexOptions.IgnoreCase)) continue;
            string text;
            try { text = File.ReadAllText(cfg); } catch { continue; }
            if (Regex.IsMatch(text, @"(password|pwd)\s*=\s*[^;""'\s<]{2,}", RegexOptions.IgnoreCase))
                configHits.Add(Path.GetRelativePath(m.Directory, cfg));
        }
        if (configHits.Count > 0)
            findings.Add(new("medium", "Security", "Database passwords stored in configuration files",
                "Connection strings with passwords are saved in plain text. Use user-secrets for development and environment variables or a secret store in production.",
                configHits.Distinct().ToList()));

        // 5. Exception details returned to callers
        var leaks = new List<string>();
        foreach (var rec in m.Endpoints.Where(e => e.Method is not null && !e.File.IsReference))
        {
            var body = rec.Method!.Body?.ToString() ?? rec.Method.ExpressionBody?.ToString() ?? "";
            if (Regex.IsMatch(body, @"\b(ex|e|exception|err)\.(Message|StackTrace|ToString\(\))"))
                leaks.Add($"{rec.Info.HttpMethod} {rec.Info.Route}");
        }
        var helperLeak = m.Files.Where(f => !f.IsReference).SelectMany(f => f.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Any(md => Regex.IsMatch(md.Identifier.Text, "Excep|Error|Fail", RegexOptions.IgnoreCase) &&Regex.IsMatch(md.Body?.ToString() ?? "", @"\.Message\b"));
        if (leaks.Count > 0 || helperLeak)
            findings.Add(new("medium", "Security", "Error details are sent back to callers",
                "Returning exception messages can reveal table names, SQL or server paths to anyone calling the API. Log the details on the server and return a short, generic message with an error number instead.",
                leaks.Count > 0 ? leaks.Take(20).ToList() : ["A shared error helper copies exception messages into responses."]));

        // 6. Unsupported .NET version
        if (caps.SupportNote is not null)
            findings.Add(new("medium", "Maintenance", $"{m.FrameworkLabel} no longer gets security updates", caps.SupportNote + " Use the upgrade assistant to move to a supported version.", []));

        // 7. ASP.NET Core packages from an older version than the project
        if (!m.IsFramework && m.FrameworkVersion >= 3)
        {
            var project = ProjectFile.Load(m.CsprojPath).Document;
            var stale = project.Descendants().Where(e => e.Name.LocalName == "PackageReference")
                .Select(e => (Id: (string?)e.Attribute("Include") ?? "", Version: (string?)e.Attribute("Version") ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == "Version")?.Value ?? ""))
                .Where(p => p.Id.StartsWith("Microsoft.AspNetCore.", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(p.Version, @"^2\."))
                .Select(p => $"{p.Id} {p.Version}").ToList();
            if (stale.Count > 0)
                findings.Add(new("low", "Maintenance", "Old ASP.NET Core 2.x packages in a newer project",
                    $"These packages are from ASP.NET Core 2 but the project targets {m.FrameworkLabel}. They're usually already included in the framework and can be removed.", stale));
        }

        // 8. CORS open to any website
        if (m.Files.Any(f => !f.IsReference && f.Text.Contains("AllowAnyOrigin()")) && eps.Any(e => e.RequiresAuth))
            findings.Add(new("low", "Security", "Any website can call this API from a browser",
                "CORS allows every origin. If browsers of your own apps are the only callers, list their addresses with WithOrigins(…) instead.", []));

        // 9. Missing descriptions
        var undocumented = eps.Where(e => string.IsNullOrWhiteSpace(e.Summary)).ToList();
        if (undocumented.Count > 0)
            findings.Add(new(undocumented.Count > eps.Count / 2 ? "low" : "info", "Documentation", $"{undocumented.Count} endpoint{Plural(undocumented.Count)} without a description",
                "Descriptions appear in Swagger/Scalar and in exported Postman collections, and help other developers and testers.",
                undocumented.Take(20).Select(e => $"{e.HttpMethod} {e.Route}").ToList(), undocumented.Select(e => e.Id).ToList()));

        // 10b. Data changes through GET
        var unsafeGets = eps.Where(e => e.HttpMethod == "GET" && Regex.IsMatch(e.Name, @"^(Delete|Remove|Insert|Add|Create|Update|Save|Set|Cancel|Approve|Post)([A-Z_]|$)")).ToList();
        if (unsafeGets.Count > 0)
            findings.Add(new("medium", "Reliability", $"{unsafeGets.Count} endpoint{Plural(unsafeGets.Count)} change data using GET",
                "Browsers, link previews, crawlers and retries can call GET requests on their own, which may delete or change data by accident. Use POST, PUT or DELETE for endpoints that change data.",
                unsafeGets.Take(20).Select(e => $"GET {e.Route}").ToList(), unsafeGets.Select(e => e.Id).ToList()));

        // 10. Reads exposed as POST
        var postReads = eps.Where(e => e.HttpMethod == "POST" && Regex.IsMatch(e.Name, @"^(Get|List|Find|Search|Load|Fetch)[A-Z]")).ToList();
        if (postReads.Count >= 3)
            findings.Add(new("info", "Design", $"{postReads.Count} read-only endpoints use POST",
                "Endpoints that only read data are normally GET, which lets browsers, proxies and tools cache and retry them safely. POST is fine when the request needs a complex body.",
                postReads.Take(15).Select(e => $"POST {e.Route}").ToList(), postReads.Select(e => e.Id).ToList()));

        var penalty = findings.Sum(f => f.Severity switch { "high" => 20, "medium" => 10, "low" => 4, _ => 0 });
        var score = Math.Clamp(100 - penalty, 0, 100);
        var grade = score >= 90 ? "Excellent" : score >= 75 ? "Good" : score >= 55 ? "Needs attention" : "At risk";
        var order = new[] { "high", "medium", "low", "info" };
        return new HealthReport(score, grade, findings.OrderBy(f => Array.IndexOf(order, f.Severity)).ToList(), checks);
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true }).ToList(); }
        catch { return []; }
    }

    private static string Shape(string route) => Regex.Replace(route.ToLowerInvariant(), @"\{[^}]*\}", "{}").TrimEnd('/');
    private static string Plural(int n) => n == 1 ? "" : "s";
}
