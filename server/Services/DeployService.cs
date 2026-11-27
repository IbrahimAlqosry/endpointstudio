using QosriStudio.Server.Models;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using QosriStudio.Server.Services.Frontend;

namespace QosriStudio.Server.Services;

public record DeployRequest(
    string Target,                       // folder | zip | docker | dockerRemote | ssh | webdeploy | azure | ftp
    string? ProfileName = null,
    string Configuration = "Release",
    string? Runtime = null,
    bool SelfContained = false,
    bool SingleFile = false,
    string? Environment = null,
    string? OutputPath = null,
    string? ImageName = null,
    string? ImageTag = null,
    bool RunLocally = false,
    int? HostPort = null,
    string? ContainerName = null,
    bool Push = false,
    string? Registry = null,
    string? RegistryUser = null,
    string? RegistryPassword = null,
    string? Host = null,
    int? Port = null,
    string? User = null,
    string? KeyFile = null,
    string? RemotePath = null,
    string? RestartCommand = null,
    string? SiteName = null,
    string? Password = null,
    string? ResourceGroup = null,
    string? AppName = null,
    bool Ftps = false,
    bool IisRewrite = false);

public record DeployProfile(string Id, string Name, DeployRequest Request, DateTime Saved);
public record DeployTools(bool Docker, bool DockerRunning, string? DockerVersion, bool Ssh, bool MsDeploy, bool AzureCli, bool VisualStudio);

/// <summary>Publishes a project and delivers it: folder, ZIP, Docker (local, registry, remote), SSH, IIS Web Deploy, Azure App Service or FTP.</summary>
public class DeployService(JobService jobs, DotnetService dotnet, HistoryService history)
{
    private static string? MsDeployPath =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(f => Path.Combine(Environment.GetFolderPath(f), "IIS", "Microsoft Web Deploy V3", "msdeploy.exe"))
            .FirstOrDefault(File.Exists);

    public static string? FindOnPath(string name)
    {
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var ext in exts)
            {
                try { var p = Path.Combine(dir.Trim(), name + ext); if (File.Exists(p)) return p; } catch { /* bad PATH entry */ }
            }
        return null;
    }

    public async Task<DeployTools> ToolsAsync()
    {
        var docker = FindOnPath("docker");
        string? version = null;
        if (docker is not null)
        {
            try
            {
                var (exit, output) = await DotnetService.RunProcessAsync(docker, ["version", "--format", "{{.Server.Version}}"], System.Environment.CurrentDirectory, TimeSpan.FromSeconds(15));
                if (exit == 0) version = output.Trim().Split('\n')[0].Trim();
            }
            catch { /* not running */ }
        }
        var tc = await dotnet.GetToolchainAsync();
        return new DeployTools(docker is not null, version is not null, version, FindOnPath("ssh") is not null, MsDeployPath is not null, FindOnPath("az") is not null, tc.MsBuildPath is not null);
    }

    // ------------------------------------------------------------------ Profiles (no passwords are ever stored)

    private static string ProfilesFile(string csproj) => Path.Combine(AppStorage.Root, "deploy", AppStorage.ProjectKey(csproj) + ".json");

    public List<DeployProfile> Profiles(string csproj)
    {
        try { return File.Exists(ProfilesFile(csproj)) ? JsonSerializer.Deserialize<List<DeployProfile>>(File.ReadAllText(ProfilesFile(csproj)), AppStorage.Json) ?? [] : []; }
        catch { return []; }
    }

    private void SaveProfile(string csproj, DeployRequest req)
    {
        var list = Profiles(csproj);
        var clean = req with { RegistryPassword = null, Password = null };
        var name = string.IsNullOrWhiteSpace(req.ProfileName) ? DefaultName(req) : req.ProfileName!.Trim();
        list.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, new DeployProfile(Guid.NewGuid().ToString("N")[..8], name, clean with { ProfileName = name }, DateTime.UtcNow));
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilesFile(csproj))!);
        File.WriteAllText(ProfilesFile(csproj), JsonSerializer.Serialize(list.Take(20).ToList(), AppStorage.Json));
    }

    private static string DefaultName(DeployRequest r) => r.Target switch
    {
        "folder" => $"Folder · {r.OutputPath}",
        "zip" => "ZIP package",
        "docker" => $"Docker · {r.ImageName}",
        "dockerRemote" => $"Docker on {r.Host}",
        "ssh" => $"{r.User}@{r.Host}",
        "webdeploy" => $"IIS · {r.Host}/{r.SiteName}",
        "azure" => $"Azure · {r.AppName}",
        "ftp" => $"FTP · {r.Host}",
        _ => r.Target,
    };

    // ------------------------------------------------------------------ Start

    public string Start(string csproj, DeployRequest req)
    {
        var full = Path.GetFullPath(csproj);
        if (Directory.Exists(full) || full.EndsWith("angular.json", StringComparison.OrdinalIgnoreCase)) full = FrontendService.NormalizePath(full);
        if (!File.Exists(full)) throw new StudioException("The project file could not be found.");
        Validate(req);
        SaveProfile(full, req);
        var title = DefaultName(req with { ProfileName = null });
        return jobs.Start(full, "deploy", title, ctx => RunAsync(ctx, full, req));
    }

    private static void Validate(DeployRequest r)
    {
        static void Need(string? v, string what) { if (string.IsNullOrWhiteSpace(v)) throw new StudioException($"Please enter {what}."); }
        switch (r.Target)
        {
            case "folder": Need(r.OutputPath, "the folder to publish to"); break;
            case "zip": Need(r.OutputPath, "where to save the ZIP file"); break;
            case "docker":
                if (r.RunLocally && r.HostPort is null or <= 0) throw new StudioException("Please enter the port to open on this computer.");
                if (r.Push) Need(r.Registry, "the registry address (for example docker.io/yourname or myregistry.azurecr.io)");
                break;
            case "dockerRemote": Need(r.Host, "the server address"); Need(r.User, "the SSH user"); if (r.HostPort is null or <= 0) throw new StudioException("Please enter the port to open on the server."); break;
            case "ssh": Need(r.Host, "the server address"); Need(r.User, "the SSH user"); Need(r.RemotePath, "the folder on the server"); break;
            case "webdeploy": Need(r.Host, "the server address"); Need(r.SiteName, "the IIS site name"); Need(r.User, "the Web Deploy user"); Need(r.Password, "the Web Deploy password"); break;
            case "azure": Need(r.ResourceGroup, "the resource group"); Need(r.AppName, "the App Service name"); break;
            case "ftp": Need(r.Host, "the FTP server"); Need(r.User, "the FTP user"); Need(r.Password, "the FTP password"); break;
            default: throw new StudioException("Choose where to publish.");
        }
        if (r.Runtime is { Length: > 0 } rid && !Regex.IsMatch(rid, @"^[a-z0-9\-]+$")) throw new StudioException("That runtime isn't valid.");
        foreach (var v in new[] { r.ImageName, r.ImageTag, r.ContainerName })
            if (v is { Length: > 0 } && !Regex.IsMatch(v, @"^[a-z0-9][a-z0-9_.\-/]*$")) throw new StudioException("Docker names can only use lowercase letters, numbers, dots, dashes and slashes.");
        if (r.Host is { Length: > 0 } h && !Regex.IsMatch(h, @"^[A-Za-z0-9.\-:\[\]]+$")) throw new StudioException("The server address can only contain letters, numbers, dots and dashes.");
        if (r.User is { Length: > 0 } u && !Regex.IsMatch(u, @"^[A-Za-z0-9._\-\\@$]+$")) throw new StudioException("The user name contains characters that aren't allowed.");
    }

    private async Task<string> RunAsync(JobService.Context ctx, string csproj, DeployRequest r)
    {
        var isFrontend = csproj.EndsWith("package.json", StringComparison.OrdinalIgnoreCase);
        var name = isFrontend ? Path.GetFileName(Path.GetDirectoryName(csproj)!) : Path.GetFileNameWithoutExtension(csproj);
        var (platform, major, legacy) = isFrontend ? (ProjectPlatform.NetCore, 0, false) : ProjectAnalyzer.Peek(csproj);
        var framework = platform == ProjectPlatform.NetFramework || legacy;
        var temp = Path.Combine(Path.GetTempPath(), "QosriStudio", "publish", $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}");

        switch (r.Target)
        {
            case "folder":
            {
                await PublishAsync(ctx, csproj, r, temp, framework);
                var dest = Path.GetFullPath(r.OutputPath!);
                await CopyWithAppOfflineAsync(ctx, temp, dest);
                return $"Published {name} to {dest}.";
            }
            case "zip":
            {
                await PublishAsync(ctx, csproj, r, temp, framework);
                var zip = ZipPath(r.OutputPath!, name);
                Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
                if (File.Exists(zip)) File.Delete(zip);
                ctx.Log($"Creating {zip}…");
                ZipFile.CreateFromDirectory(temp, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
                return $"Created {zip} ({new FileInfo(zip).Length / 1024 / 1024.0:0.0} MB).";
            }
            case "docker":
            case "dockerRemote":
            {
                if (framework) throw new StudioException(".NET Framework apps need Windows containers, which QosriStudio can't build. Publish to a folder or IIS instead.");
                var docker = FindOnPath("docker") ?? throw new StudioException("Docker isn't installed. Install Docker Desktop and try again.");
                var image = isFrontend ? await BuildFrontendImageAsync(ctx, docker, csproj, r) : await BuildImageAsync(ctx, docker, csproj, r, major);
                var port = isFrontend ? 80 : 8080;
                if (r.Target == "docker") return await DockerLocalOrPushAsync(ctx, docker, image, r, port);
                return await DockerRemoteAsync(ctx, docker, image, r, port);
            }
            case "ssh":
            {
                var ssh = FindOnPath("ssh") ?? throw new StudioException("The OpenSSH client isn't installed (Windows: Settings → Apps → Optional features → OpenSSH Client).");
                var scp = FindOnPath("scp") ?? ssh.Replace("ssh", "scp");
                await PublishAsync(ctx, csproj, r with { Runtime = string.IsNullOrEmpty(r.Runtime) ? null : r.Runtime }, temp, framework);
                var archive = temp + ".tgz";
                ctx.Log("Packing the published files…");
                await using (var fs = File.Create(archive))
                await using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
                    await TarFile.CreateFromDirectoryAsync(temp, gz, includeBaseDirectory: false, ctx.Token);
                var remoteTmp = $"/tmp/{name.ToLowerInvariant()}-{DateTime.Now:yyyyMMddHHmmss}.tgz";
                var target = $"{r.User}@{r.Host}";
                var common = SshOptions(r);
                if (await JobService.RunAsync(ctx, scp, [.. common.Select(o => o == "-p" ? "-P" : o), archive, $"{target}:{remoteTmp}"], Path.GetTempPath()) != 0)
                    throw new StudioException("Copying to the server failed. Check the address, the user and that your SSH key is allowed on the server.");
                var path = r.RemotePath!.TrimEnd('/');
                var command = $"mkdir -p '{path}' && tar -xzf '{remoteTmp}' -C '{path}' && rm -f '{remoteTmp}'" + (string.IsNullOrWhiteSpace(r.RestartCommand) ? "" : $" && {r.RestartCommand}");
                if (await JobService.RunAsync(ctx, ssh, [.. common, target, command], Path.GetTempPath()) != 0)
                    throw new StudioException("The files were copied, but unpacking or restarting on the server failed. See the log.");
                File.Delete(archive);
                return $"Deployed {name} to {r.Host}:{path}.";
            }
            case "webdeploy":
            {
                var msdeploy = MsDeployPath ?? throw new StudioException("Web Deploy isn't installed on this computer. Install \"Web Deploy 4\" from Microsoft and try again.");
                await PublishAsync(ctx, csproj, r, temp, framework);
                var host = r.Host!.Contains("://") ? r.Host! : $"https://{r.Host}:8172/msdeploy.axd";
                var site = r.SiteName!;
                var dest = $"-dest:contentPath=\"{site}\",computerName=\"{host}?site={site}\",userName=\"{r.User}\",password=\"{r.Password}\",authType=\"Basic\"";
                var exit = await JobService.RunAsync(ctx, msdeploy, ["-verb:sync", $"-source:contentPath=\"{temp}\"", dest, "-allowUntrusted", "-enableRule:AppOffline"], temp, [r.Password!]);
                if (exit != 0) throw new StudioException("Web Deploy failed. Check the server address, the site name and the user's permissions.");
                return $"Deployed {name} to the IIS site {site} on {r.Host}.";
            }
            case "azure":
            {
                var az = FindOnPath("az") ?? throw new StudioException("The Azure CLI isn't installed. Install it and run “az login” once, then try again.");
                await PublishAsync(ctx, csproj, r, temp, framework);
                var zip = temp + ".zip";
                ZipFile.CreateFromDirectory(temp, zip, CompressionLevel.Optimal, false);
                var exit = await JobService.RunAsync(ctx, az, ["webapp", "deploy", "--resource-group", r.ResourceGroup!, "--name", r.AppName!, "--src-path", zip, "--type", "zip"], Path.GetTempPath());
                if (exit != 0) throw new StudioException("Azure didn't accept the deployment. Make sure you ran “az login” and that the App Service exists.");
                ctx.ResultUrl = $"https://{r.AppName}.azurewebsites.net";
                return $"Deployed {name} to Azure App Service {r.AppName}.";
            }
            case "ftp":
            {
                await PublishAsync(ctx, csproj, r, temp, framework);
                await FtpUploadAsync(ctx, temp, r);
                return $"Uploaded {name} to {r.Host}.";
            }
        }
        throw new StudioException("Choose where to publish.");
    }

    // ------------------------------------------------------------------ Publish

    private async Task PublishAsync(JobService.Context ctx, string csproj, DeployRequest r, string output, bool framework)
    {
        Directory.CreateDirectory(output);
        if (csproj.EndsWith("package.json", StringComparison.OrdinalIgnoreCase))
        {
            // Frontend: build it, then treat its output folder like published files
            var model = FrontendAnalyzer.Analyze(csproj);
            var built = await FrontendService.BuildAsync(ctx, model);
            foreach (var file in Directory.EnumerateFiles(built, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(output, Path.GetRelativePath(built, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
            var webConfig = Path.Combine(output, "web.config");
            if (r.IisRewrite && !File.Exists(webConfig))
            {
                ctx.Log("Adding web.config so IIS sends page refreshes to the app (needs the IIS URL Rewrite module).");
                await File.WriteAllTextAsync(webConfig, SpaWebConfig);
            }
            return;
        }
        var dir = Path.GetDirectoryName(csproj)!;
        int exit;
        if (framework)
        {
            var tc = await dotnet.GetToolchainAsync();
            var msbuild = tc.MsBuildPath ?? throw new StudioException("Publishing .NET Framework projects needs Visual Studio or the Visual Studio Build Tools.");
            ctx.Log("Publishing with MSBuild (Visual Studio)…");
            exit = await JobService.RunAsync(ctx, msbuild, [csproj, "-restore", "-p:RestorePackagesConfig=true", $"-p:Configuration={r.Configuration}",
                "-p:DeployOnBuild=true", "-p:WebPublishMethod=FileSystem", $"-p:PublishUrl={output}", "-p:DeleteExistingFiles=true", "-nologo", "-v:m"], dir);
        }
        else
        {
            ctx.Log("Publishing with the .NET SDK…");
            var args = new List<string> { "publish", csproj, "-c", r.Configuration, "-o", output, "-nologo" };
            if (!string.IsNullOrWhiteSpace(r.Runtime))
            {
                args.AddRange(["-r", r.Runtime!, "--self-contained", r.SelfContained ? "true" : "false"]);
                if (r.SingleFile) args.Add("-p:PublishSingleFile=true");
            }
            exit = await JobService.RunAsync(ctx, "dotnet", args, dir);
        }
        if (exit != 0) throw new StudioException("Publishing failed. The log above shows why (usually build errors: use “Check build”).");
        ctx.Log($"Published files: {output}");
    }

    private static async Task CopyWithAppOfflineAsync(JobService.Context ctx, string from, string to)
    {
        Directory.CreateDirectory(to);
        var offline = Path.Combine(to, "app_offline.htm");
        var iis = File.Exists(Path.Combine(to, "web.config"));
        if (iis)
        {
            ctx.Log("Taking the IIS site offline while files are replaced (app_offline.htm)…");
            await File.WriteAllTextAsync(offline, "<html><body><h3>Updating… back in a moment.</h3></body></html>");
            await Task.Delay(2500, ctx.Token);
        }
        try
        {
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                for (var attempt = 0; ; attempt++)
                {
                    try { File.Copy(file, target, true); break; }
                    catch (IOException) when (attempt < 5) { await Task.Delay(1000, ctx.Token); }
                }
                count++;
            }
            ctx.Log($"Copied {count} files.");
        }
        finally
        {
            if (iis && File.Exists(offline)) { File.Delete(offline); ctx.Log("The site is back online."); }
        }
    }

    private static string ZipPath(string output, string name)
    {
        var p = Path.GetFullPath(output);
        return p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? p : Path.Combine(p, $"{name}-{DateTime.Now:yyyyMMdd-HHmm}.zip");
    }

    // ------------------------------------------------------------------ Docker

    private async Task<string> BuildImageAsync(JobService.Context ctx, string docker, string csproj, DeployRequest r, int major)
    {
        var dir = Path.GetDirectoryName(csproj)!;
        var sln = ProjectAnalyzer.FindSolution(csproj);
        var context = sln is not null ? Path.GetDirectoryName(sln)! : dir;
        var dockerfile = Path.Combine(dir, "Dockerfile");
        var changes = history.Begin(csproj, "Added a Dockerfile");
        if (!File.Exists(dockerfile))
        {
            ctx.Log("No Dockerfile yet, creating one for this project (you can undo this from History)…");
            changes.Write(dockerfile, Dockerfile(csproj, context, major));
        }
        var ignore = Path.Combine(context, ".dockerignore");
        if (!File.Exists(ignore)) changes.Write(ignore, "**/bin/\r\n**/obj/\r\n**/.vs/\r\n**/.git/\r\n**/node_modules/\r\n**/*.user\r\n");
        changes.Commit();

        var image = (string.IsNullOrWhiteSpace(r.ImageName) ? Path.GetFileNameWithoutExtension(csproj).ToLowerInvariant().Replace('.', '-') : r.ImageName!) + ":" + (string.IsNullOrWhiteSpace(r.ImageTag) ? "latest" : r.ImageTag);
        ctx.Log($"Building the image {image} (the first build downloads the .NET images and can take a few minutes)…");
        if (await JobService.RunAsync(ctx, docker, ["build", "-f", dockerfile, "-t", image, context], context) != 0)
            throw new StudioException("The Docker image couldn't be built. If Docker Desktop isn't running, start it; otherwise the log shows the build error.");
        return image;
    }

    public static string Dockerfile(string csproj, string context, int major)
    {
        var pf = ProjectFile.Load(csproj);
        var assembly = pf.Property("AssemblyName") ?? Path.GetFileNameWithoutExtension(csproj);
        var rel = Path.GetRelativePath(context, csproj).Replace('\\', '/');
        var (sdk, aspnet) = major switch
        {
            2 => ("mcr.microsoft.com/dotnet/core/sdk:2.1", "mcr.microsoft.com/dotnet/core/aspnet:2.1"),
            3 => ("mcr.microsoft.com/dotnet/sdk:3.1", "mcr.microsoft.com/dotnet/aspnet:3.1"),
            >= 5 => ($"mcr.microsoft.com/dotnet/sdk:{major}.0", $"mcr.microsoft.com/dotnet/aspnet:{major}.0"),
            _ => ("mcr.microsoft.com/dotnet/sdk:10.0", "mcr.microsoft.com/dotnet/aspnet:10.0"),
        };
        return $"""
            # Created by QosriStudio. Build from the solution folder:
            #   docker build -f {rel.Replace(Path.GetFileName(csproj), "Dockerfile")} -t {assembly.ToLowerInvariant()} .
            FROM {sdk} AS build
            WORKDIR /src
            COPY . .
            RUN dotnet restore "{rel}"
            RUN dotnet publish "{rel}" -c Release -o /app/publish /p:UseAppHost=false

            FROM {aspnet} AS final
            WORKDIR /app
            COPY --from=build /app/publish .
            ENV ASPNETCORE_URLS=http://+:8080
            EXPOSE 8080
            ENTRYPOINT ["dotnet", "{assembly}.dll"]

            """.Replace("\n", "\r\n");
    }

    private const string SpaWebConfig = """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <system.webServer>
            <rewrite>
              <rules>
                <rule name="Single page app" stopProcessing="true">
                  <match url=".*" />
                  <conditions logicalGrouping="MatchAll">
                    <add input="{REQUEST_FILENAME}" matchType="IsFile" negate="true" />
                    <add input="{REQUEST_FILENAME}" matchType="IsDirectory" negate="true" />
                  </conditions>
                  <action type="Rewrite" url="/" />
                </rule>
              </rules>
            </rewrite>
          </system.webServer>
        </configuration>
        """;

    private async Task<string> BuildFrontendImageAsync(JobService.Context ctx, string docker, string packageJson, DeployRequest r)
    {
        var m = FrontendAnalyzer.Analyze(packageJson);
        var dockerfile = Path.Combine(m.Directory, "Dockerfile");
        var changes = history.Begin(packageJson, "Added a Dockerfile");
        if (!File.Exists(dockerfile))
        {
            ctx.Log("No Dockerfile yet, creating one (Node to build, nginx to serve). You can undo this from History.");
            changes.Write(dockerfile, FrontendDockerfile(m));
            changes.Write(Path.Combine(m.Directory, "nginx.conf"),
                "server {\r\n  listen 80;\r\n  root /usr/share/nginx/html;\r\n  index index.html;\r\n  location / {\r\n    try_files $uri $uri/ /index.html;\r\n  }\r\n}\r\n");
        }
        var ignore = Path.Combine(m.Directory, ".dockerignore");
        if (!File.Exists(ignore)) changes.Write(ignore, "node_modules\r\ndist\r\nbuild\r\n.angular\r\n.next\r\n.git\r\n");
        changes.Commit();
        var image = (string.IsNullOrWhiteSpace(r.ImageName) ? Regex.Replace(m.Name.ToLowerInvariant(), "[^a-z0-9._-]", "-") : r.ImageName!) + ":" + (string.IsNullOrWhiteSpace(r.ImageTag) ? "latest" : r.ImageTag);
        ctx.Log($"Building the image {image}…");
        if (await JobService.RunAsync(ctx, docker, ["build", "-t", image, m.Directory], m.Directory) != 0)
            throw new StudioException("The Docker image couldn't be built. If Docker Desktop isn't running, start it; otherwise the log shows the build error.");
        return image;
    }

    private static string FrontendDockerfile(FrontendModel m)
    {
        // Older Angular / CRA toolchains don't build on new Node versions
        var node = m.Framework == "Angular"
            ? m.FrameworkMajor switch { > 0 and <= 11 => "14", <= 15 => "16", <= 17 => "18", _ => "22" }
            : m.NeedsLegacyOpenSsl ? "16" : "22";
        var install = m.PackageManager switch
        {
            "yarn" => "RUN corepack enable && yarn install --frozen-lockfile",
            "pnpm" => "RUN corepack enable && pnpm install --frozen-lockfile",
            _ => File.Exists(Path.Combine(m.Directory, "package-lock.json")) ? "RUN npm ci" + (m.Framework == "Angular" && m.FrameworkMajor is > 0 and < 13 ? " --legacy-peer-deps" : "") : "RUN npm install",
        };
        var output = (m.OutputDir ?? "dist").Replace('\\', '/').TrimEnd('/');
        return $"""
            # Created by QosriStudio: builds the app with Node, then serves it with nginx
            FROM node:{node}-alpine AS build
            WORKDIR /app
            COPY package*.json yarn.lock* pnpm-lock.yaml* ./
            {install}
            COPY . .
            RUN {(m.PackageManager == "npm" ? "npm run build" : m.PackageManager + " run build")}

            FROM nginx:alpine
            COPY nginx.conf /etc/nginx/conf.d/default.conf
            COPY --from=build /app/{output} /usr/share/nginx/html
            EXPOSE 80

            """.Replace("\n", "\r\n");
    }

    private static async Task<string> DockerLocalOrPushAsync(JobService.Context ctx, string docker, string image, DeployRequest r, int containerPort = 8080)
    {
        var messages = new List<string> { $"Built the image {image}." };
        if (r.Push)
        {
            var registry = r.Registry!.Trim().TrimEnd('/');
            var registryHost = registry.Split('/')[0];
            if (!string.IsNullOrWhiteSpace(r.RegistryUser) && !string.IsNullOrWhiteSpace(r.RegistryPassword))
            {
                if (await JobService.RunAsync(ctx, docker, ["login", registryHost.Contains('.') ? registryHost : "docker.io", "-u", r.RegistryUser!, "--password-stdin"], Path.GetTempPath(), [r.RegistryPassword!], r.RegistryPassword) != 0)
                    throw new StudioException("Signing in to the registry failed. Check the user name and password (or access token).");
            }
            var remote = $"{registry}/{image}";
            if (await JobService.RunAsync(ctx, docker, ["tag", image, remote], Path.GetTempPath()) != 0 ||
                await JobService.RunAsync(ctx, docker, ["push", remote], Path.GetTempPath()) != 0)
                throw new StudioException("Pushing the image failed. Make sure you're signed in to the registry and allowed to push there.");
            messages.Add($"Pushed it to {remote}.");
        }
        if (r.RunLocally)
        {
            var container = string.IsNullOrWhiteSpace(r.ContainerName) ? image.Split(':')[0].Replace('/', '-') : r.ContainerName!;
            await JobService.RunAsync(ctx, docker, ["rm", "-f", container], Path.GetTempPath());
            var args = new List<string> { "run", "-d", "--restart", "unless-stopped", "--name", container, "-p", $"{r.HostPort}:{containerPort}" };
            if (!string.IsNullOrWhiteSpace(r.Environment)) args.AddRange(["-e", $"ASPNETCORE_ENVIRONMENT={r.Environment}"]);
            args.Add(image);
            if (await JobService.RunAsync(ctx, docker, args, Path.GetTempPath()) != 0)
                throw new StudioException($"The container couldn't start. Port {r.HostPort} may already be in use.");
            ctx.ResultUrl = $"http://localhost:{r.HostPort}";
            messages.Add($"It's running in the container {container} at http://localhost:{r.HostPort}.");
        }
        return string.Join(" ", messages);
    }

    private static async Task<string> DockerRemoteAsync(JobService.Context ctx, string docker, string image, DeployRequest r, int containerPort = 8080)
    {
        var host = $"ssh://{r.User}@{r.Host}" + (r.Port is > 0 and not 22 ? $":{r.Port}" : "");
        var tar = Path.Combine(Path.GetTempPath(), "QosriStudio", $"{image.Replace(':', '-').Replace('/', '-')}.tar");
        Directory.CreateDirectory(Path.GetDirectoryName(tar)!);
        ctx.Log("Saving the image to send it to the server…");
        if (await JobService.RunAsync(ctx, docker, ["save", "-o", tar, image], Path.GetTempPath()) != 0) throw new StudioException("The image couldn't be saved.");
        ctx.Log($"Sending the image to {r.Host} (this can take a while on slow connections)…");
        if (await JobService.RunAsync(ctx, docker, ["-H", host, "load", "-i", tar], Path.GetTempPath()) != 0)
            throw new StudioException("The server didn't accept the image. Check that Docker is installed there and that your SSH key can sign in.");
        File.Delete(tar);
        var container = string.IsNullOrWhiteSpace(r.ContainerName) ? image.Split(':')[0].Replace('/', '-') : r.ContainerName!;
        await JobService.RunAsync(ctx, docker, ["-H", host, "rm", "-f", container], Path.GetTempPath());
        var args = new List<string> { "-H", host, "run", "-d", "--restart", "unless-stopped", "--name", container, "-p", $"{r.HostPort}:{containerPort}" };
        if (!string.IsNullOrWhiteSpace(r.Environment)) args.AddRange(["-e", $"ASPNETCORE_ENVIRONMENT={r.Environment}"]);
        args.Add(image);
        if (await JobService.RunAsync(ctx, docker, args, Path.GetTempPath()) != 0) throw new StudioException("The container couldn't start on the server. See the log.");
        ctx.ResultUrl = $"http://{r.Host}:{r.HostPort}";
        return $"Running on {r.Host} in the container {container}, port {r.HostPort}.";
    }

    private static List<string> SshOptions(DeployRequest r)
    {
        var o = new List<string> { "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new" };
        if (r.Port is > 0) o.AddRange(["-p", r.Port.Value.ToString()]);
        if (!string.IsNullOrWhiteSpace(r.KeyFile)) o.AddRange(["-i", r.KeyFile!]);
        return o;
    }

    // ------------------------------------------------------------------ FTP

#pragma warning disable SYSLIB0014 // FtpWebRequest is old but built in and works with every FTP server
    private static async Task FtpUploadAsync(JobService.Context ctx, string from, DeployRequest r)
    {
        var baseUri = (r.Host!.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ? r.Host! : $"ftp://{r.Host}" + (r.Port is > 0 ? $":{r.Port}" : ""))
                      .TrimEnd('/') + "/" + (r.RemotePath ?? "").Trim('/');
        var credentials = new NetworkCredential(r.User, r.Password);
        FtpWebRequest Req(string uri, string method)
        {
            var q = (FtpWebRequest)WebRequest.Create(uri);
            q.Method = method; q.Credentials = credentials; q.EnableSsl = r.Ftps; q.UsePassive = true; q.UseBinary = true; q.KeepAlive = true;
            return q;
        }
        void MakeDir(string uri)
        {
            try { using var _ = (FtpWebResponse)Req(uri, WebRequestMethods.Ftp.MakeDirectory).GetResponse(); }
            catch (WebException) { /* usually "already exists" */ }
        }
        if ((r.RemotePath ?? "").Trim('/').Length > 0) MakeDir(baseUri);
        foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories).OrderBy(d => d.Length))
            MakeDir($"{baseUri}/{Path.GetRelativePath(from, d).Replace('\\', '/')}");
        var files = Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList();
        var n = 0;
        foreach (var f in files)
        {
            ctx.Token.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(from, f).Replace('\\', '/');
            try
            {
                var q = Req($"{baseUri}/{rel}", WebRequestMethods.Ftp.UploadFile);
                await using (var s = await q.GetRequestStreamAsync()) await using (var fs = File.OpenRead(f)) await fs.CopyToAsync(s, ctx.Token);
                using var _ = (FtpWebResponse)await q.GetResponseAsync();
            }
            catch (WebException ex) { throw new StudioException($"Uploading {rel} failed: {ex.Message}"); }
            if (++n % 20 == 0 || n == files.Count) ctx.Log($"Uploaded {n} of {files.Count} files…");
        }
    }
#pragma warning restore SYSLIB0014

    // ------------------------------------------------------------------ Solution build

    /// <summary>Build, rebuild or clean one project or a whole solution, in Debug or Release.</summary>
    public string BuildTarget(string path, string action, string configuration)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new StudioException("The project or solution file could not be found.");
        action = action is "rebuild" or "clean" ? action : "build";
        configuration = configuration is "Release" ? "Release" : "Debug";
        var verb = action == "rebuild" ? "Rebuild" : action == "clean" ? "Clean" : "Build";
        return jobs.Start(full, "build", $"{verb} {Path.GetFileNameWithoutExtension(full)} ({configuration})", async ctx =>
        {
            var dir = Path.GetDirectoryName(full)!;
            var text = File.ReadAllText(full);
            var legacy = text.Contains("349c5851-65df-11da-9384-00065b846f21", StringComparison.OrdinalIgnoreCase)
                         || full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && !text.Contains("Sdk=\"", StringComparison.OrdinalIgnoreCase)
                         || File.Exists(Path.Combine(dir, "packages.config"));
            int exit;
            if (legacy)
            {
                var tc = await dotnet.GetToolchainAsync();
                var msbuild = tc.MsBuildPath ?? throw new StudioException("This has .NET Framework projects, which need Visual Studio or the Build Tools to build.");
                exit = await JobService.RunAsync(ctx, msbuild, [full, $"-t:{verb}", "-restore", "-p:RestorePackagesConfig=true", $"-p:Configuration={configuration}", "-nologo", "-v:m"], dir);
            }
            else if (action == "clean") exit = await JobService.RunAsync(ctx, "dotnet", ["clean", full, "-c", configuration, "-nologo", "-v", "m"], dir);
            else exit = await JobService.RunAsync(ctx, "dotnet", action == "rebuild" ? ["build", full, "-c", configuration, "--no-incremental", "-nologo", "-v", "m"] : ["build", full, "-c", configuration, "-nologo", "-v", "m"], dir);
            if (exit != 0) throw new StudioException(action == "clean" ? "Cleaning failed. See the log." : "There are build errors. They're listed in the log (lines with “error”).");
            return action == "clean" ? "Build output removed." : "Built without errors.";
        });
    }

    public string BuildSolution(string slnPath)
    {
        var full = Path.GetFullPath(slnPath);
        if (!File.Exists(full)) throw new StudioException("The solution file could not be found.");
        return jobs.Start(full, "build", $"Build {Path.GetFileNameWithoutExtension(full)}", async ctx =>
        {
            var dir = Path.GetDirectoryName(full)!;
            var legacy = File.ReadAllText(full).Contains("349c5851-65df-11da-9384-00065b846f21", StringComparison.OrdinalIgnoreCase)
                         || Directory.EnumerateFiles(dir, "packages.config", SearchOption.AllDirectories).Any();
            int exit;
            if (legacy)
            {
                var tc = await dotnet.GetToolchainAsync();
                var msbuild = tc.MsBuildPath ?? throw new StudioException("This solution has .NET Framework projects, which need Visual Studio or the Build Tools to build.");
                exit = await JobService.RunAsync(ctx, msbuild, [full, "-restore", "-p:RestorePackagesConfig=true", "-nologo", "-v:m"], dir);
            }
            else exit = await JobService.RunAsync(ctx, "dotnet", ["build", full, "-nologo", "-v", "m"], dir);
            var log = string.Join("\n", ctx.Token.IsCancellationRequested ? [] : new[] { "" });
            _ = log;
            if (exit != 0) throw new StudioException("The solution has build errors. They're listed in the log (lines with “error”).");
            return "The whole solution builds without errors.";
        });
    }
}
