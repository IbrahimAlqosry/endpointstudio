using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EndpointStudio.Server.Models;

namespace EndpointStudio.Server.Services;

/// <summary>Builds projects, runs them and sends test requests to them.</summary>
public class DotnetService : IDisposable
{
    private static readonly Regex IssueRx = new(@"^\s*(?<file>[^\s].*?)\((?<line>\d+),\d+\): (?<kind>error|warning) (?<code>[A-Z]+\d+): (?<msg>.*?)(?: \[[^\]]+\])?$", RegexOptions.Compiled);
    private readonly ConcurrentDictionary<string, RunningApp> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    // ------------------------------------------------------------------ Build

    public async Task<BuildResult> BuildAsync(string csproj, CancellationToken ct = default)
    {
        if (IsRunning(csproj))
            throw new StudioException("Stop the running app before checking the build.");
        var sw = Stopwatch.StartNew();
        var (exit, output) = await RunProcessAsync("dotnet", ["build", csproj, "-nologo", "-clp:NoSummary", "-v", "q"], Path.GetDirectoryName(csproj)!, TimeSpan.FromMinutes(5), ct);
        return ParseBuild(exit, output, sw.ElapsedMilliseconds, Path.GetDirectoryName(csproj)!);
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
                    errors.Add(new BuildIssue(null, null, codeEnd > 0 ? rest[..codeEnd] : "", codeEnd > 0 ? rest[(codeEnd + 1)..].Trim() : rest));
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
            errors.Add(new BuildIssue(null, null, "BUILD", string.IsNullOrWhiteSpace(tail) ? "The build failed." : tail));
        }
        return new BuildResult(exit == 0, errors.Count, warnings.Count, errors, ms);
    }

    // ------------------------------------------------------------------ Run

    private sealed class RunningApp
    {
        public required Process Process { get; init; }
        public required string Url { get; init; }
        public string State { get; set; } = "starting";
        public ConcurrentQueue<string> Log { get; } = new();
        public void Append(string line)
        {
            Log.Enqueue(line);
            while (Log.Count > 300) Log.TryDequeue(out _);
        }
    }

    public bool IsRunning(string csproj) =>
        _running.TryGetValue(Path.GetFullPath(csproj), out var app) && !app.Process.HasExited;

    public RunStatus Status(string csproj)
    {
        if (!_running.TryGetValue(Path.GetFullPath(csproj), out var app))
            return new RunStatus(false, null, "stopped", []);
        if (app.Process.HasExited && app.State is "starting" or "running")
            app.State = app.State == "starting" ? "failed" : "stopped";
        return new RunStatus(!app.Process.HasExited, app.Url, app.State, app.Log.ToList());
    }

    public RunStatus Start(string csproj)
    {
        var key = Path.GetFullPath(csproj);
        if (_running.TryGetValue(key, out var existing) && !existing.Process.HasExited) return Status(csproj);

        var port = FreePort();
        var url = $"http://localhost:{port}";
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(key)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "run", "--project", key, "--no-launch-profile", "--urls", url }) psi.ArgumentList.Add(a);
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var app = new RunningApp { Process = proc, Url = url };
        app.Append("Building and starting the app…");
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            app.Append(e.Data);
            if (e.Data.Contains("Now listening on") || e.Data.Contains("Application started")) app.State = "running";
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) app.Append(e.Data); };
        proc.Exited += (_, _) =>
        {
            app.Append(app.State == "running" ? "The app stopped." : "The app couldn't start. See the messages above.");
            app.State = app.State == "running" || app.State == "stopping" ? "stopped" : "failed";
        };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _running[key] = app;
        return Status(csproj);
    }

    public RunStatus Stop(string csproj)
    {
        if (_running.TryGetValue(Path.GetFullPath(csproj), out var app) && !app.Process.HasExited)
        {
            app.State = "stopping";
            try { app.Process.Kill(entireProcessTree: true); app.Process.WaitForExit(5000); } catch { /* already gone */ }
            app.State = "stopped";
        }
        return Status(csproj);
    }

    public async Task<TryResponse> TryAsync(string csproj, TryRequest req, CancellationToken ct)
    {
        if (!_running.TryGetValue(Path.GetFullPath(csproj), out var app) || app.Process.HasExited || app.State != "running")
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
        using var p = Process.Start(psi) ?? throw new StudioException($"Couldn't start {file}.");
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
            try { if (!app.Process.HasExited) app.Process.Kill(true); } catch { /* ignore */ }
        _http.Dispose();
    }
}
