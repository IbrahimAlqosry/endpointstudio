using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QosriStudio.Server.Models;

namespace QosriStudio.Server.Services;

/// <summary>Knows which .NET tools are installed, builds and runs projects, and sends test requests to them.</summary>
public class DotnetService : IDisposable
{
    private static readonly Regex IssueRx = new(@"^\s*(?<file>[^\s].*?)\((?<line>\d+),\d+\): (?<kind>error|warning) (?<code>[A-Z]+\d+): (?<msg>.*?)(?: \[[^\]]+\])?$", RegexOptions.Compiled);
    private readonly ConcurrentDictionary<string, RunningApp> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };
    private Toolchain? _toolchain;
    private readonly SemaphoreSlim _toolchainLock = new(1, 1);

    // ------------------------------------------------------------------ Toolchain

    public sealed record Toolchain(List<int> SdkMajors, List<int> AspNetRuntimeMajors, string? MsBuildPath, string? IisExpressPath, string SdkVersion);

    public async Task<Toolchain> GetToolchainAsync()
    {
        if (_toolchain is not null) return _toolchain;
        await _toolchainLock.WaitAsync();
        try
        {
            if (_toolchain is not null) return _toolchain;
            var dir = Environment.CurrentDirectory;
            var (_, sdks) = await RunProcessAsync("dotnet", ["--list-sdks"], dir, TimeSpan.FromSeconds(30));
            var (_, runtimes) = await RunProcessAsync("dotnet", ["--list-runtimes"], dir, TimeSpan.FromSeconds(30));
            var (_, version) = await RunProcessAsync("dotnet", ["--version"], dir, TimeSpan.FromSeconds(30));
            var sdkMajors = Regex.Matches(sdks, @"^(\d+)\.", RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(x => x).ToList();
            var rtMajors = Regex.Matches(runtimes, @"^Microsoft\.AspNetCore\.App (\d+)\.", RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(x => x).ToList();
            _toolchain = new Toolchain(sdkMajors, rtMajors, await FindMsBuildAsync(), FindIisExpress(), version.Trim());
            return _toolchain;
        }
        finally { _toolchainLock.Release(); }
    }

    private static async Task<string?> FindMsBuildAsync()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        try
        {
            var (_, output) = await RunProcessAsync(vswhere, ["-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe"], Environment.CurrentDirectory, TimeSpan.FromSeconds(30));
            return output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.EndsWith("MSBuild.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(l));
        }
        catch { return null; }
    }

    private static string? FindIisExpress()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var p = Path.Combine(Environment.GetFolderPath(root), "IIS Express", "iisexpress.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>What the studio can do with a project, given the tools installed on this computer.</summary>
    public async Task<ProjectCapabilities> CapabilitiesAsync(ProjectModel m)
    {
        var tc = await GetToolchainAsync();
        if (m.IsFramework)
        {
            var canBuild = tc.MsBuildPath is not null;
            var canRun = canBuild && tc.IisExpressPath is not null;
            var outOfSupport = Regex.IsMatch(m.FrameworkLabel, @"4\.(0|5|5\.1|5\.2|6|6\.1)$");
            return new ProjectCapabilities(
                "NetFramework", m.FrameworkLabel, false, false, canBuild, canRun,
                canBuild ? null : "Building .NET Framework projects needs Visual Studio or the Visual Studio Build Tools.",
                canRun ? null : tc.MsBuildPath is null ? "Running .NET Framework projects needs Visual Studio (with IIS Express)." : "Running .NET Framework projects needs IIS Express, which comes with Visual Studio.",
                outOfSupport ? $"{m.FrameworkLabel} is no longer supported by Microsoft. Consider moving to .NET Framework 4.8." : null);
        }

        var major = m.FrameworkVersion;
        var buildOk = major == 0 || tc.SdkMajors.Any(s => s >= major);
        var runOk = major == 0 || tc.AspNetRuntimeMajors.Contains(major);
        string? support = major switch
        {
            0 => null,
            < 8 => $"{m.FrameworkLabel} is no longer supported by Microsoft and doesn't get security fixes. Consider upgrading.",
            8 or 9 => $"{m.FrameworkLabel} reached end of support in November 2026. Consider upgrading to .NET 10 (LTS).",
            _ => null,
        };
        return new ProjectCapabilities(
            "NetCore", m.FrameworkLabel, m.SupportsMinimalApis, true, buildOk, runOk,
            buildOk ? null : $"This project needs the .NET {major} SDK (or newer) to build. Installed: {string.Join(", ", tc.SdkMajors.Select(s => $".NET {s}"))}.",
            runOk ? null : $"Running this project needs the ASP.NET Core {major} runtime, which isn't installed. You can still edit and build it.",
            support);
    }

    /// <summary>Versions a new project can target with the installed SDKs.</summary>
    public async Task<List<TargetOption>> TargetOptionsAsync()
    {
        var tc = await GetToolchainAsync();
        var maxSdk = tc.SdkMajors.DefaultIfEmpty(0).Max();
        var list = new List<TargetOption>();
        foreach (var major in Codegen.ProjectGenerator.SupportedTargets.Keys.OrderByDescending(x => x))
        {
            if (major > maxSdk) continue;
            var support = major switch
            {
                10 => "LTS · supported until Nov 2028",
                11 => "STS · supported until Nov 2027",
                8 or 9 => "Support ended Nov 2026",
                _ => "Out of support",
            };
            list.Add(new TargetOption($"net{major}.0", major, $".NET {major}", support, true, tc.AspNetRuntimeMajors.Contains(major), false));
        }
        var recommended = list.FirstOrDefault(t => t.Major == 10) ?? list.FirstOrDefault();
        return list.Select(t => t == recommended ? t with { Recommended = true } : t).ToList();
    }

    // ------------------------------------------------------------------ Build

    public async Task<BuildResult> BuildAsync(string csproj, CancellationToken ct = default)
    {
        if (IsRunning(csproj))
            throw new StudioException("Stop the running app before checking the build.");
        var sw = Stopwatch.StartNew();
        var dir = Path.GetDirectoryName(Path.GetFullPath(csproj))!;
        var (exit, output) = await BuildProcessAsync(csproj, line => { }, ct);
        return ParseBuild(exit, output, sw.ElapsedMilliseconds, dir);
    }

    private async Task<(int Exit, string Output)> BuildProcessAsync(string csproj, Action<string> log, CancellationToken ct)
    {
        var (platform, _, legacy) = ProjectAnalyzer.Peek(csproj);
        var dir = Path.GetDirectoryName(Path.GetFullPath(csproj))!;
        if (platform == ProjectPlatform.NetFramework || legacy)
        {
            var tc = await GetToolchainAsync();
            var msbuild = tc.MsBuildPath ?? throw new StudioException("Building .NET Framework projects needs Visual Studio or the Visual Studio Build Tools, which weren't found on this computer.");
            log("Building with MSBuild (Visual Studio)…");
            return await RunProcessAsync(msbuild,
                [csproj, "-restore", "-p:RestorePackagesConfig=true", $"-p:SolutionDir={SolutionDirFor(dir)}", "-p:Configuration=Debug", "-nologo", "-v:m", "-clp:NoSummary"],
                dir, TimeSpan.FromMinutes(8), ct);
        }
        log("Building with the .NET SDK…");
        return await RunProcessAsync("dotnet", ["build", csproj, "-nologo", "-clp:NoSummary", "-v", "q"], dir, TimeSpan.FromMinutes(5), ct);
    }

    /// <summary>Package restore for old projects puts packages next to the solution file.</summary>
    private static string SolutionDirFor(string projectDir)
    {
        var d = new DirectoryInfo(projectDir);
        for (var i = 0; i < 4 && d is not null; i++, d = d.Parent)
            if (d.EnumerateFiles("*.sln").Any() || d.EnumerateFiles("*.slnx").Any()) return d.FullName.TrimEnd('\\') + "\\";
        return (Directory.GetParent(projectDir)?.FullName ?? projectDir).TrimEnd('\\') + "\\";
    }

    private static BuildResult ParseBuild(int exit, string output, long ms, string projectDir)
    {
        var errors = new List<BuildIssue>();
        var warnings = new HashSet<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = IssueRx.Match(line);
            if (!m.Success)
            {
                if (line.Contains(": error ") && errors.Count < 50)
                {
                    var idx = line.IndexOf(": error ", StringComparison.Ordinal);
                    var rest = Regex.Replace(line[(idx + 8)..], @" \[[^\]]+\]$", "");
                    var codeEnd = rest.IndexOf(':');
                    errors.Add(new BuildIssue(null, null, codeEnd > 0 ? rest[..codeEnd] : "", FriendlyBuildMessage(codeEnd > 0 ? rest[(codeEnd + 1)..].Trim() : rest)));
                }
                continue;
            }
            if (m.Groups["kind"].Value == "warning") { warnings.Add(line); continue; }
            var file = m.Groups["file"].Value;
            try { file = Path.GetRelativePath(projectDir, file).Replace('\\', '/'); } catch { /* keep */ }
            errors.Add(new BuildIssue(file, int.Parse(m.Groups["line"].Value), m.Groups["code"].Value, m.Groups["msg"].Value));
        }
        errors = errors.DistinctBy(e => (e.File, e.Line, e.Code, e.Message)).ToList();
        if (exit != 0 && errors.Count == 0)
        {
            var tail = string.Join("\n", output.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(6));
            errors.Add(new BuildIssue(null, null, "BUILD", string.IsNullOrWhiteSpace(tail) ? "The build failed." : FriendlyBuildMessage(tail)));
        }
        return new BuildResult(exit == 0, errors.Count, warnings.Count, errors, ms);
    }

    private static string FriendlyBuildMessage(string msg)
    {
        var m = Regex.Match(msg, @"NETSDK1045: The current \.NET SDK does not support targeting \.NET (\d+\.\d+)");
        if (m.Success) return $"This project needs a newer .NET SDK (version {m.Groups[1].Value}) to build. Install it from dot.net.";
        if (msg.Contains("NU1101") || msg.Contains("NU1301")) return "Some packages couldn't be downloaded. Check your internet connection and try again.";
        if (msg.Contains("MSB4019") && msg.Contains("WebApplication.targets"))
            return "The ASP.NET web build tools are missing. Install the \"ASP.NET and web development\" workload in Visual Studio.";
        return msg;
    }

    // ------------------------------------------------------------------ Run

    private sealed class RunningApp
    {
        public Process? Process { get; set; }
        public required string Url { get; init; }
        public string State { get; set; } = "starting";
        public CancellationTokenSource Cancel { get; } = new();
        public ConcurrentQueue<string> Log { get; } = new();
        public bool Exited => Process is null ? State is "failed" or "stopped" : Process.HasExited;
        public void Append(string line)
        {
            Log.Enqueue(line);
            while (Log.Count > 400) Log.TryDequeue(out _);
        }
    }

    public bool IsRunning(string csproj) =>
        _running.TryGetValue(Path.GetFullPath(csproj), out var app) && !app.Exited;

    public RunStatus Status(string csproj)
    {
        if (!_running.TryGetValue(Path.GetFullPath(csproj), out var app))
            return new RunStatus(false, null, "stopped", []);
        if (app.Process is { HasExited: true } && app.State is "starting" or "running")
            app.State = app.State == "starting" ? "failed" : "stopped";
        return new RunStatus(!app.Exited, app.Url, app.State, app.Log.ToList());
    }

    public RunStatus Start(string csproj)
    {
        var key = Path.GetFullPath(csproj);
        if (_running.TryGetValue(key, out var existing) && !existing.Exited) return Status(csproj);

        var port = FreePort();
        var app = new RunningApp { Url = $"http://localhost:{port}" };
        _running[key] = app;
        var (platform, _, legacy) = ProjectAnalyzer.Peek(key);
        if (platform == ProjectPlatform.NetFramework || legacy) _ = Task.Run(() => StartFrameworkAsync(key, app, port));
        else StartCore(key, app);
        return Status(csproj);
    }

    private void StartCore(string csproj, RunningApp app)
    {
        var psi = NewPsi("dotnet", Path.GetDirectoryName(csproj)!, ["run", "--project", csproj, "--no-launch-profile", "--urls", app.Url]);
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        app.Append("Building and starting the app…");
        Launch(app, psi, line => line.Contains("Now listening on") || line.Contains("Application started"));
    }

    private async Task StartFrameworkAsync(string csproj, RunningApp app, int port)
    {
        try
        {
            var tc = await GetToolchainAsync();
            if (tc.IisExpressPath is null) throw new StudioException("Running .NET Framework projects needs IIS Express, which comes with Visual Studio.");
            var (exit, output) = await BuildProcessAsync(csproj, app.Append, app.Cancel.Token);
            if (exit != 0)
            {
                foreach (var l in output.Split('\n').Where(l => l.Contains(": error ")).Take(10)) app.Append(l.Trim());
                app.Append("The app couldn't be built. Use “Check build” to see the problems.");
                app.State = "failed";
                return;
            }
            if (app.State == "stopped") return;
            app.Append("Starting IIS Express…");
            var psi = NewPsi(tc.IisExpressPath, Path.GetDirectoryName(csproj)!, [$"/path:{Path.GetDirectoryName(csproj)}", $"/port:{port}", "/systray:false"]);
            Launch(app, psi, line => line.Contains("IIS Express is running", StringComparison.OrdinalIgnoreCase));
        }
        catch (StudioException ex) { app.Append(ex.Message); app.State = "failed"; }
        catch (OperationCanceledException) { app.State = "stopped"; }
        catch (Exception ex) { app.Append("The app couldn't start: " + ex.Message); app.State = "failed"; }
    }

    private static ProcessStartInfo NewPsi(string file, string workDir, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        return psi;
    }

    private static void Launch(RunningApp app, ProcessStartInfo psi, Func<string, bool> isReady)
    {
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            app.Append(e.Data);
            if (isReady(e.Data)) app.State = "running";
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) app.Append(e.Data); };
        proc.Exited += (_, _) =>
        {
            var log = string.Join("\n", app.Log);
            var missing = Regex.Match(log, @"Framework: 'Microsoft\.(?:AspNetCore|NETCore)\.App', version '(\d+)\.");
            if (app.State != "running" && missing.Success)
                app.Append($"This app needs the .NET {missing.Groups[1].Value} runtime, which isn't installed. Install the ASP.NET Core {missing.Groups[1].Value} Runtime from dot.net to run it.");
            else
                app.Append(app.State == "running" || app.State == "stopping" ? "The app stopped." : "The app couldn't start. See the messages above.");
            app.State = app.State == "running" || app.State == "stopping" ? "stopped" : "failed";
        };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        app.Process = proc;
    }

    public RunStatus Stop(string csproj)
    {
        if (_running.TryGetValue(Path.GetFullPath(csproj), out var app) && !app.Exited)
        {
            app.State = "stopping";
            app.Cancel.Cancel();
            try { if (app.Process is { HasExited: false } p) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } } catch { /* already gone */ }
            app.State = "stopped";
        }
        return Status(csproj);
    }

    public async Task<TryResponse> TryAsync(string csproj, TryRequest req, CancellationToken ct)
    {
        if (!_running.TryGetValue(Path.GetFullPath(csproj), out var app) || app.Exited || app.State != "running")
            throw new StudioException("Start the app first, then try the endpoint.");

        var path = req.Path.StartsWith('/') ? req.Path : "/" + req.Path;
        using var msg = new HttpRequestMessage(new HttpMethod(req.Method.ToUpperInvariant()), app.Url + path);
        if (!string.IsNullOrEmpty(req.Body) && req.Method.ToUpperInvariant() is not ("GET" or "HEAD"))
            msg.Content = new StringContent(req.Body, Encoding.UTF8, "application/json");
        foreach (var (k, v) in req.Headers ?? [])
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            if (!msg.Headers.TryAddWithoutValidation(k, v)) msg.Content?.Headers.TryAddWithoutValidation(k, v);
        }

        var sw = Stopwatch.StartNew();
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(msg, ct); }
        catch (HttpRequestException ex) { throw new StudioException("The app didn't respond: " + ex.Message); }
        catch (TaskCanceledException) { throw new StudioException("The request took too long (over 60 seconds)."); }
        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            sw.Stop();
            var contentType = resp.Content.Headers.ContentType?.MediaType;
            if (contentType?.Contains("json") == true)
            {
                try { body = JsonSerializer.Serialize(JsonDocument.Parse(body).RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); }
                catch { /* not valid json, keep raw */ }
            }
            if (body.Length > 500_000) body = body[..500_000] + "\n… (response cut off, too large to show)";
            var headers = resp.Headers.Concat(resp.Content.Headers).ToDictionary(h => h.Key, h => string.Join(", ", h.Value));
            return new TryResponse((int)resp.StatusCode, resp.ReasonPhrase ?? ((HttpStatusCode)resp.StatusCode).ToString(), headers, body, sw.ElapsedMilliseconds, contentType);
        }
    }

    // ------------------------------------------------------------------ Helpers

    public static async Task<(int Exit, string Output)> RunProcessAsync(string file, IEnumerable<string> args, string workDir, TimeSpan timeout, CancellationToken ct = default)
    {
        using var p = Process.Start(NewPsi(file, workDir, args)) ?? throw new StudioException($"Couldn't start {file}.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* ignore */ }
            throw new StudioException("The operation took too long and was stopped.");
        }
        return (p.ExitCode, await stdout + "\n" + await stderr);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        foreach (var app in _running.Values)
            try { app.Cancel.Cancel(); if (app.Process is { HasExited: false } p) p.Kill(true); } catch { /* ignore */ }
        _http.Dispose();
    }
}
