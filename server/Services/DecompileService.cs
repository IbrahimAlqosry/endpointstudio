using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using EndpointStudio.Server.Models;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;

namespace EndpointStudio.Server.Services;

public record AssemblyEntry(string Name, string Path, string? Version, string? Framework, bool IsOwnCode, bool IsMainApp, int Controllers, int Types, long Size, bool Obfuscated = false);
public record InspectResult(string Folder, List<AssemblyEntry> Assemblies, string? MainApp);
public record RecoverRequest(string Folder, List<string> Assemblies, string OutputFolder, string? SolutionName);

/// <summary>
/// Recovers C# source from published .NET files (DLL/EXE) with ICSharpCode.Decompiler, the engine behind ILSpy.
/// Meant for code you own or have the right to inspect: lost sources, old deployments, shared DLLs.
/// </summary>
public class DecompileService(JobService jobs)
{
    // Libraries that come from Microsoft or NuGet packages, not from the user's own code
    private static readonly Regex ThirdParty = new(
        @"^(Microsoft|System|Newtonsoft|Swashbuckle|Oracle\.(ManagedDataAccess|EntityFrameworkCore|DataAccess)|DotNetZip|Ionic\.|Npgsql|MySql|MySqlConnector|Pomelo|Serilog|AutoMapper|FluentValidation|Dapper|Polly|StackExchange|Azure|Google|AWSSDK|NLog|log4net|BouncyCastle|Portable\.BouncyCastle|ICSharpCode|Humanizer|MediatR|Hangfire|Quartz|RestSharp|Scalar|SQLitePCLRaw|mscorlib|netstandard|EntityFramework|Antlr|WebGrease|Owin|Elmah|DocumentFormat|ClosedXML|EPPlus|itext|QRCoder|SixLabors|SkiaSharp|Twilio|SendGrid|MailKit|MimeKit|Castle|Autofac|Ninject|Unity|Moq|xunit|nunit|NuGet|Grpc|Google\.Protobuf|Npoi|NPOI|CsvHelper|Bogus|MongoDB|RabbitMQ|Confluent|Elasticsearch|Nest|Sentry|OpenTelemetry|Mono\.|Remotion|Z\.EntityFramework|Ardalis|IdentityModel|IdentityServer4|Duende|Aspose)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public InspectResult Inspect(string path)
    {
        var folder = File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path))! : Path.GetFullPath(path);
        if (!Directory.Exists(folder)) throw new StudioException("That folder doesn't exist.");
        var candidates = Directory.EnumerateFiles(folder, "*.dll").Concat(Directory.EnumerateFiles(folder, "*.exe"));
        // ASP.NET (Framework) sites keep their code in bin\
        var bin = Path.Combine(folder, "bin");
        if (Directory.Exists(bin)) candidates = candidates.Concat(Directory.EnumerateFiles(bin, "*.dll"));

        var mains = Directory.EnumerateFiles(folder, "*.runtimeconfig.json").Select(f => Path.GetFileName(f).Replace(".runtimeconfig.json", "")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<AssemblyEntry>();
        foreach (var file in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entry = Describe(file, mains);
            if (entry is not null) list.Add(entry);
        }
        if (list.Count == 0) throw new StudioException("No .NET files were found in that folder. Pick the folder that contains the published app (with its .dll files).");
        list = list.OrderByDescending(a => a.IsMainApp).ThenByDescending(a => a.IsOwnCode).ThenByDescending(a => a.Controllers).ThenBy(a => a.Name).ToList();
        var main = list.FirstOrDefault(a => a.IsMainApp)?.Name ?? list.FirstOrDefault(a => a.Controllers > 0 && a.IsOwnCode)?.Name;
        return new InspectResult(folder, list, main);
    }

    private static AssemblyEntry? Describe(string file, HashSet<string> mains)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null; // native DLL
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return null;
            var asm = md.GetAssemblyDefinition();
            var name = md.GetString(asm.Name);
            string? framework = null;
            foreach (var h in asm.GetCustomAttributes())
            {
                var attr = md.GetCustomAttribute(h);
                if (AttributeTypeName(md, attr) != "TargetFrameworkAttribute") continue;
                var blob = md.GetBlobReader(attr.Value);
                if (blob.ReadUInt16() == 1) framework = blob.ReadSerializedString();
            }
            var controllers = 0;
            var types = 0;
            var scrambled = 0;
            foreach (var th in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(th);
                if ((t.Attributes & System.Reflection.TypeAttributes.Public) == 0) continue;
                types++;
                var typeName = md.GetString(t.Name);
                // Obfuscators rename types to long random runs of capital letters (or unprintable names)
                if (System.Text.RegularExpressions.Regex.IsMatch(typeName, "^[A-Z]{20,}$") || typeName.Any(ch => ch > 0x2000 || char.IsControl(ch))) scrambled++;
                if (typeName.EndsWith("Controller") && !t.BaseType.IsNil && BaseName(md, t.BaseType) is { } b && (b.EndsWith("Controller") || b.EndsWith("ControllerBase") || b == "ApiController")) controllers++;
            }
            var obfuscated = types > 0 && scrambled * 4 >= types;
            return new AssemblyEntry(name, file, asm.Version.ToString(), FriendlyFramework(framework), !ThirdParty.IsMatch(name) && !obfuscated, mains.Contains(name), controllers, types, new FileInfo(file).Length, obfuscated);
        }
        catch { return null; }
    }

    private static string? AttributeTypeName(MetadataReader md, CustomAttribute attr) => attr.Constructor.Kind switch
    {
        HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent is var p && p.Kind == HandleKind.TypeReference
            ? md.GetString(md.GetTypeReference((TypeReferenceHandle)p).Name) : null,
        HandleKind.MethodDefinition => md.GetString(md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor).GetDeclaringType()).Name),
        _ => null,
    };

    private static string? BaseName(MetadataReader md, EntityHandle h) => h.Kind switch
    {
        HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)h).Name),
        HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)h).Name),
        _ => null,
    };

    private static string? FriendlyFramework(string? f)
    {
        if (f is null) return null;
        var m = Regex.Match(f, @"^\.NET(?:CoreApp|Framework|Standard)?,Version=v([\d.]+)");
        if (!m.Success) return f;
        var v = m.Groups[1].Value;
        if (f.StartsWith(".NETFramework")) return $".NET Framework {v}";
        if (f.StartsWith(".NETStandard")) return $".NET Standard {v}";
        var major = int.TryParse(v.Split('.')[0], out var mj) ? mj : 0;
        return major <= 3 ? $".NET Core {v}" : $".NET {major}";
    }

    public string Recover(RecoverRequest req)
    {
        if (req.Assemblies.Count == 0) throw new StudioException("Choose at least one file to recover.");
        var output = Path.GetFullPath(req.OutputFolder);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new StudioException("The output folder isn't empty. Choose an empty or new folder so nothing is overwritten.");
        var inspect = Inspect(req.Folder);
        var chosen = inspect.Assemblies.Where(a => req.Assemblies.Contains(a.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (chosen.Count == 0) throw new StudioException("Those files weren't found in the folder.");
        var slnName = string.IsNullOrWhiteSpace(req.SolutionName) ? (inspect.MainApp ?? chosen[0].Name) : req.SolutionName!.Trim();

        return jobs.Start(output, "decompile", $"Recover {chosen.Count} project{(chosen.Count == 1 ? "" : "s")}", async ctx =>
        {
            Directory.CreateDirectory(output);
            var projects = new List<string>();
            foreach (var a in chosen)
            {
                ctx.Token.ThrowIfCancellationRequested();
                ctx.Log($"Recovering {a.Name} ({a.Types} types)…");
                var dir = Path.Combine(output, a.Name);
                Directory.CreateDirectory(dir);
                await Task.Run(() =>
                {
                    using var module = new PEFile(a.Path);
                    var resolver = new UniversalAssemblyResolver(a.Path, false, module.DetectTargetFrameworkId());
                    resolver.AddSearchDirectory(inspect.Folder);
                    var bin = Path.Combine(inspect.Folder, "bin");
                    if (Directory.Exists(bin)) resolver.AddSearchDirectory(bin);
                    var settings = new DecompilerSettings(ICSharpCode.Decompiler.CSharp.LanguageVersion.Latest)
                    {
                        ThrowOnAssemblyResolveErrors = false,
                        UseSdkStyleProjectFormat = WholeProjectDecompiler.CanUseSdkStyleProjectFormat(module),
                    };
                    var decompiler = new WholeProjectDecompiler(settings, resolver, null, null, null);
                    var done = 0;
                    decompiler.ProgressIndicator = new Progress<DecompilationProgress>(p =>
                    {
                        if (p.TotalUnits > 0 && ++done % 25 == 0) ctx.Log($"  {a.Name}: {Math.Min(100, p.UnitsCompleted * 100 / p.TotalUnits)}%");
                    });
                    decompiler.DecompileProject(module, dir, ctx.Token);
                }, ctx.Token);
                var csproj = Directory.EnumerateFiles(dir, "*.csproj").FirstOrDefault();
                if (csproj is not null) projects.Add(csproj);
                ctx.Log($"  {a.Name}: {Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Count()} code files.");
            }

            // Link the recovered projects together and put them in one solution
            ctx.Log("Creating the solution…");
            await JobService.RunAsync(ctx, "dotnet", ["new", "sln", "-n", slnName, "-o", output, "--format", "sln"], output);
            var sln = Path.Combine(output, slnName + ".sln");
            if (File.Exists(sln) && projects.Count > 0) await JobService.RunAsync(ctx, "dotnet", ["sln", sln, "add", .. projects], output);
            LinkProjects(projects, chosen);
            ctx.ResultUrl = "studio:" + (File.Exists(sln) ? sln : projects.FirstOrDefault() ?? output);
            return $"Recovered {projects.Count} project{(projects.Count == 1 ? "" : "s")} into {output}. Recovered code can differ from the original (names of local variables, comments and formatting are lost), so check the build before relying on it.";
        });
    }

    /// <summary>The decompiler references sibling assemblies as DLL files; point them at the recovered projects instead.</summary>
    private static void LinkProjects(List<string> projects, List<AssemblyEntry> chosen)
    {
        var byName = projects.ToDictionary(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase);
        foreach (var csproj in projects)
        {
            var text = File.ReadAllText(csproj);
            var changed = false;
            foreach (var other in chosen.Select(c => c.Name).Where(n => byName.ContainsKey(n) && !n.Equals(Path.GetFileNameWithoutExtension(csproj), StringComparison.OrdinalIgnoreCase)))
            {
                var rx = new Regex($@"<Reference Include=""{Regex.Escape(other)}""\s*>.*?</Reference>|<Reference Include=""{Regex.Escape(other)}""\s*/>", RegexOptions.Singleline);
                if (!rx.IsMatch(text)) continue;
                var rel = Path.GetRelativePath(Path.GetDirectoryName(csproj)!, byName[other]);
                text = rx.Replace(text, $"<ProjectReference Include=\"{rel}\" />", 1);
                changed = true;
            }
            if (changed) File.WriteAllText(csproj, text);
        }
    }
}
