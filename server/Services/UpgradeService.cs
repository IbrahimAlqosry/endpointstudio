using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using QosriStudio.Server.Models;

namespace QosriStudio.Server.Services;

public record UpgradeStep(string Project, string Kind, string Title, string Detail);
public record UpgradePlan(string From, string To, int TargetMajor, List<UpgradeStep> Steps, List<string> Warnings, bool CanApply, List<string> Projects);
public record UpgradeRequest(int TargetMajor, bool IncludeReferences);

/// <summary>Moves SDK-style projects to a newer .NET version: target framework, version-matched packages and obsolete packages.</summary>
public class UpgradeService(HistoryService history, DotnetService dotnet)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly ConcurrentDictionary<string, List<string>> VersionCache = new(StringComparer.OrdinalIgnoreCase);

    // Packages whose major version follows the .NET version
    private static readonly string[] FrameworkPackagePrefixes =
        ["Microsoft.AspNetCore.", "Microsoft.EntityFrameworkCore", "Microsoft.Extensions.", "System.Text.Json", "Microsoft.Data.Sqlite"];

    private sealed record Edit(string Path, string Description);

    public async Task<UpgradePlan> PlanAsync(string csproj, UpgradeRequest req, CancellationToken ct)
    {
        var (plan, _) = await BuildAsync(csproj, req, apply: false, ct);
        return plan;
    }

    public async Task<string> ApplyAsync(string csproj, UpgradeRequest req, CancellationToken ct)
    {
        var (plan, changes) = await BuildAsync(csproj, req, apply: true, ct);
        if (!plan.CanApply) throw new StudioException(plan.Warnings.FirstOrDefault() ?? "This project can't be upgraded automatically.");
        if (changes is null || changes.ChangedFiles.Count == 0) throw new StudioException("Nothing needs to change: the project already targets that version.");
        // Never save a project file that no longer loads
        foreach (var file in changes.ChangedFiles)
        {
            try { System.Xml.Linq.XDocument.Parse(changes.Read(file)); }
            catch (Exception ex) { throw new StudioException($"The upgrade would have damaged {Path.GetFileName(file)} ({ex.Message}), so nothing was changed."); }
        }
        changes.Commit();
        var fixes = await FixDowngradesAsync(csproj, changes.ChangedFiles.ToList(), ct);
        var total = plan.Steps.Count + fixes;
        return $"Upgraded to .NET {req.TargetMajor}. {total} change{(total == 1 ? "" : "s")} made."
               + (fixes > 0 ? $" {fixes} related package{(fixes == 1 ? " was" : "s were")} also updated so the versions fit together." : "");
    }

    /// <summary>
    /// Newer framework packages often need newer versions of packages the project pins directly (NuGet error NU1605,
    /// "package downgrade"). Restore, read those errors and raise the pinned versions. Only files the upgrade already
    /// changed are touched, so one Undo still reverts everything.
    /// </summary>
    private static async Task<int> FixDowngradesAsync(string csproj, List<string> changedFiles, CancellationToken ct)
    {
        var fixes = 0;
        var projectFiles = changedFiles.Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();
        for (var round = 0; round < 3; round++)
        {
            var (_, output) = await DotnetService.RunProcessAsync("dotnet", ["restore", csproj, "-nologo"], Path.GetDirectoryName(csproj)!, TimeSpan.FromMinutes(5), ct);
            var needed = Regex.Matches(output, @"Detected package downgrade: (?<id>[\w.\-]+) from (?<req>[\d.]+) to (?<cur>[\d.]+)")
                .GroupBy(m => m.Groups["id"].Value, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Id: g.Key, Version: g.Select(m => m.Groups["req"].Value).OrderByDescending(v => Version.TryParse(v, out var x) ? x : new Version()).First()))
                .ToList();
            if (needed.Count == 0) break;
            var changedThisRound = false;
            foreach (var file in projectFiles)
            {
                var text = await File.ReadAllTextAsync(file, ct);
                var updated = text;
                foreach (var (id, version) in needed)
                    updated = Regex.Replace(updated, $@"(<PackageReference\s+Include=""{Regex.Escape(id)}""\s+Version="")[^""]*("")", "${1}" + version + "${2}", RegexOptions.IgnoreCase);
                if (updated == text) continue;
                System.Xml.Linq.XDocument.Parse(updated);
                await File.WriteAllTextAsync(file, updated, ct);
                fixes += needed.Count(n => Regex.IsMatch(text, $@"Include=""{Regex.Escape(n.Id)}""", RegexOptions.IgnoreCase));
                changedThisRound = true;
            }
            if (!changedThisRound) break;
        }
        return fixes;
    }

    private async Task<(UpgradePlan Plan, HistoryService.ChangeSet? Changes)> BuildAsync(string csproj, UpgradeRequest req, bool apply, CancellationToken ct)
    {
        var model = ProjectAnalyzer.Analyze(csproj);
        var to = $".NET {req.TargetMajor}";
        var warnings = new List<string>();
        var steps = new List<UpgradeStep>();

        if (model.IsFramework)
            return (new UpgradePlan(model.FrameworkLabel, to, req.TargetMajor, [], [
                ".NET Framework projects can't be upgraded automatically: moving to modern .NET means rewriting the web layer (System.Web → ASP.NET Core). " +
                "Create a new project with the database wizard and move the logic over, or use Microsoft's .NET Upgrade Assistant in Visual Studio."], false, [model.Name]), null);
        if (!Codegen.ProjectGenerator.SupportedTargets.ContainsKey(req.TargetMajor))
            throw new StudioException("Choose .NET 6 or newer as the target.");
        var tc = await dotnet.GetToolchainAsync();
        if (tc.SdkMajors.Count > 0 && tc.SdkMajors.Max() < req.TargetMajor)
            warnings.Add($"The .NET {req.TargetMajor} SDK isn't installed, so the upgraded project won't build on this computer until it is.");
        if (model.FrameworkVersion >= req.TargetMajor)
            return (new UpgradePlan(model.FrameworkLabel, to, req.TargetMajor, [], [$"{model.Name} already targets {model.FrameworkLabel}."], false, [model.Name]), null);

        var projects = new List<string> { model.CsprojPath };
        if (req.IncludeReferences)
            projects.AddRange(model.ReferencedProjects.Select(r => r.Path).Where(p =>
            {
                var (plat, major, legacy) = ProjectAnalyzer.Peek(p);
                return plat == ProjectPlatform.NetCore && !legacy && major > 0 && major < req.TargetMajor;
            }));

        var changes = history.Begin(model.CsprojPath, $"Upgraded to .NET {req.TargetMajor}");
        var newTfm = $"net{req.TargetMajor}.0";
        var target = Codegen.ProjectGenerator.SupportedTargets[req.TargetMajor];
        var touchedProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in projects)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var pf = ProjectFile.Load(path);
            var oldTfm = pf.TargetFramework;
            var (_, oldMajor, oldLabel) = ProjectAnalyzer.DescribeFramework(oldTfm);
            if (oldMajor >= req.TargetMajor) continue;

            // --- Target framework (project file or the shared Directory.Build.props that defines it)
            var csText = changes.Read(path);
            var tfmRx = new Regex(@"(<TargetFrameworks?>)([^<]*)(</TargetFrameworks?>)");
            if (tfmRx.IsMatch(csText))
            {
                csText = tfmRx.Replace(csText, m => m.Groups[1].Value + string.Join(";", m.Groups[2].Value.Split(';').Select(t => t.Trim() == oldTfm ? newTfm : t)) + m.Groups[3].Value, 1);
                steps.Add(new UpgradeStep(name, "framework", $"Target {to} instead of {oldLabel}", "Changes the target framework in the project file."));
            }
            else if (FindPropsDefining(path, "TargetFramework") is { } props)
            {
                if (touchedProps.Add(props))
                {
                    var text = changes.Read(props);
                    changes.Write(props, tfmRx.Replace(text, m => m.Groups[1].Value + newTfm + m.Groups[3].Value, 1));
                    steps.Add(new UpgradeStep(name, "framework", $"Target {to} instead of {oldLabel}", $"The version is set in a shared file ({Path.GetFileName(Path.GetDirectoryName(props))}\\Directory.Build.props), so every project that uses it moves to {to} too."));
                    warnings.Add($"The target version is shared through {props}. All projects under that folder will be upgraded.");
                }
            }
            else
            {
                warnings.Add($"Couldn't find where {name} sets its .NET version.");
                continue;
            }

            // --- Packages
            var isWeb = pf.IsWeb;
            var needsFrameworkRef = false;
            var pkgRx = new Regex(@"<PackageReference\s+Include=""(?<id>[^""]+)""(?<mid>[^>]*?)(\s+Version=""(?<ver>[^""]*)"")?(?<rest>[^>]*?)(/>|>)", RegexOptions.IgnoreCase);
            // Edit from the end of the file backwards so earlier positions stay valid.
            var pkgSteps = new List<UpgradeStep>();
            foreach (Match m in pkgRx.Matches(csText).Cast<Match>().OrderByDescending(x => x.Index).ToList())
            {
                var id = m.Groups["id"].Value;
                var ver = m.Groups["ver"].Value;
                if (ver.Length == 0) ver = CentralVersion(path, id) ?? "";
                if (ver.Length == 0) continue;
                var verMajor = int.TryParse(ver.Split('.')[0], out var vm) ? vm : 0;

                // ASP.NET Core 2.x packages: part of the shared framework since 3.0
                if (id.StartsWith("Microsoft.AspNetCore.", StringComparison.OrdinalIgnoreCase) && verMajor == 2 && oldMajor >= 2 &&
                    !id.Contains("Authentication.", StringComparison.OrdinalIgnoreCase) && !id.Contains("SpaServices", StringComparison.OrdinalIgnoreCase))
                {
                    csText = RemoveElement(csText, m);
                    if (!isWeb) needsFrameworkRef = true;
                    pkgSteps.Add(new UpgradeStep(name, "remove", $"Remove {id} {ver}", "It's an old ASP.NET Core 2 package; the same code is now built into the framework."));
                    continue;
                }

                string? newVersion = null;
                var swagger = Regex.Match(csText, @"Include=""Swashbuckle\.AspNetCore[^""]*""\s+Version=""(\d+)");
                var oldSwagger = swagger.Success && int.Parse(swagger.Groups[1].Value) < 10;
                if (id.Equals("Microsoft.AspNetCore.OpenApi", StringComparison.OrdinalIgnoreCase) && req.TargetMajor >= 10 && oldSwagger)
                {
                    // ASP.NET Core OpenAPI 10 uses Microsoft.OpenApi 2.x, which Swagger UI (Swashbuckle) 6–9 can't use.
                    newVersion = verMajor < 9 ? await LatestAsync(id, 9, ct) : null;
                    warnings.Add($"{name} uses Swagger (Swashbuckle). Microsoft.AspNetCore.OpenApi was kept on version 9, because version 10 needs Swashbuckle 10, which changes the Swagger setup code.");
                }
                else if (FrameworkPackagePrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase)) && verMajor >= 2 && verMajor < req.TargetMajor && verMajor <= Math.Max(oldMajor, 3) + 1)
                    newVersion = await LatestAsync(id, req.TargetMajor, ct);
                else if (id.Equals("Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.OrdinalIgnoreCase)) newVersion = Newer(ver, target.Npgsql);
                else if (id.Equals("Pomelo.EntityFrameworkCore.MySql", StringComparison.OrdinalIgnoreCase)) newVersion = Newer(ver, target.Pomelo);
                else if (id.Equals("Oracle.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)) newVersion = Newer(ver, target.OracleEf);
                else if (id.Equals("Swashbuckle.AspNetCore", StringComparison.OrdinalIgnoreCase) && verMajor < 6) newVersion = "6.9.0";

                if (newVersion is null || newVersion == ver) continue;
                if (m.Groups["ver"].Success)
                {
                    var g = m.Groups["ver"];
                    csText = csText[..g.Index] + newVersion + csText[(g.Index + g.Length)..];
                }
                else if (FindCentralPackages(path) is { } central)
                {
                    var ctext = changes.Read(central);
                    changes.Write(central, Regex.Replace(ctext, $@"(<PackageVersion\s+Include=""{Regex.Escape(id)}""\s+Version="")[^""]*("")", "${1}" + newVersion + "${2}", RegexOptions.IgnoreCase));
                }
                pkgSteps.Add(new UpgradeStep(name, "package", $"Update {id} {ver} → {newVersion}", "Uses the release that matches the new .NET version."));
            }
            pkgSteps.Reverse();
            steps.AddRange(pkgSteps);

            if (needsFrameworkRef && !csText.Contains("Microsoft.AspNetCore.App"))
            {
                var nl = csText.Contains("\r\n") ? "\r\n" : "\n";
                var idx = csText.LastIndexOf("</Project>", StringComparison.Ordinal);
                csText = csText.Insert(idx, $"  <ItemGroup>{nl}    <FrameworkReference Include=\"Microsoft.AspNetCore.App\" />{nl}  </ItemGroup>{nl}{nl}");
                steps.Add(new UpgradeStep(name, "package", "Use the built-in ASP.NET Core framework", "Replaces the removed ASP.NET Core 2 packages in this library."));
            }

            if (csText != changes.Read(path)) changes.Write(path, csText);

            // --- Things that need a person to look at
            var sources = string.Concat(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.cs", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                .Where(f => !Regex.IsMatch(f, @"[\\/](bin|obj)[\\/]")).Take(400).Select(f => { try { return File.ReadAllText(f); } catch { return ""; } }));
            if (oldMajor <= 2 && sources.Contains("UseMvc("))
                warnings.Add($"{name} uses app.UseMvc(), which needs endpoint routing turned off (or replacing with UseRouting + MapControllers) on .NET Core 3 and newer. The build check will point to it.");
            if (oldMajor <= 2 && sources.Contains("IHostingEnvironment"))
                warnings.Add($"{name} uses IHostingEnvironment; on newer .NET it's called IWebHostEnvironment. It still compiles but shows a warning.");
            if (oldMajor <= 5 && sources.Contains("Newtonsoft") && !csText.Contains("NewtonsoftJson"))
                warnings.Add($"{name} uses Newtonsoft.Json. ASP.NET Core 3+ uses System.Text.Json by default, so JSON property names or formats may change unless you add Microsoft.AspNetCore.Mvc.NewtonsoftJson.");
        }

        if (steps.Count == 0) warnings.Add("Nothing needs to change.");
        var plan = new UpgradePlan(model.FrameworkLabel, to, req.TargetMajor, steps, warnings.Distinct().ToList(), steps.Count > 0,
            projects.Select(Path.GetFileNameWithoutExtension).ToList()!);
        return (plan, apply ? changes : null);
    }

    private static string RemoveElement(string text, Match m)
    {
        var start = m.Index;
        var end = m.Index + m.Length;
        if (!m.Value.EndsWith("/>"))
        {
            var close = text.IndexOf("</PackageReference>", end, StringComparison.OrdinalIgnoreCase);
            if (close >= 0) end = close + "</PackageReference>".Length;
        }
        while (start > 0 && text[start - 1] is ' ' or '\t') start--;
        if (end < text.Length && text[end] == '\r') end++;
        if (end < text.Length && text[end] == '\n') end++;
        return text[..start] + text[end..];
    }

    private static string? Newer(string current, string candidate) =>
        Version.TryParse(Regex.Match(current, @"^[\d.]+").Value, out var a) && Version.TryParse(candidate, out var b) && b > a ? candidate : null;

    private static async Task<string?> LatestAsync(string id, int major, CancellationToken ct)
    {
        if (!VersionCache.TryGetValue(id, out var versions))
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.nuget.org/v3-flatcontainer/{id.ToLowerInvariant()}/index.json", ct));
                versions = doc.RootElement.GetProperty("versions").EnumerateArray().Select(v => v.GetString()!).Where(v => !v.Contains('-')).ToList();
                VersionCache[id] = versions;
            }
            catch { return null; } // offline: leave the version as it is
        }
        return versions.Where(v => v.StartsWith(major + ".")).LastOrDefault();
    }

    private static string? FindPropsDefining(string csproj, string property)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(csproj)!);
        for (var i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
        {
            var props = Path.Combine(dir.FullName, "Directory.Build.props");
            if (File.Exists(props) && File.ReadAllText(props).Contains($"<{property}>")) return props;
        }
        return null;
    }

    private static string? FindCentralPackages(string csproj)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(csproj)!);
        for (var i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "Directory.Packages.props");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string? CentralVersion(string csproj, string id)
    {
        var central = FindCentralPackages(csproj);
        if (central is null) return null;
        var m = Regex.Match(File.ReadAllText(central), $@"<PackageVersion\s+Include=""{Regex.Escape(id)}""\s+Version=""([^""]*)""", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
