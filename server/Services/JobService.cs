using Stackwright.Server.Models;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Stackwright.Server.Services;

public record JobStatus(string Id, string Kind, string Title, string State, List<string> Log, string? Result, string? ResultUrl, DateTime Started, DateTime? Finished);

/// <summary>Long-running work (builds, publishing, deployments) with a live log the UI can follow.</summary>
public class JobService
{
    private sealed class Job
    {
        public required string Id { get; init; }
        public required string Kind { get; init; }
        public required string Title { get; init; }
        public required string Owner { get; init; }
        public string State { get; set; } = "running";
        public string? Result { get; set; }
        public string? ResultUrl { get; set; }
        public DateTime Started { get; } = DateTime.UtcNow;
        public DateTime? Finished { get; set; }
        public ConcurrentQueue<string> Log { get; } = new();
        public CancellationTokenSource Cancel { get; } = new();
    }

    public sealed class Context(Action<string> log, CancellationToken ct)
    {
        public CancellationToken Token => ct;
        public void Log(string line) => log(line);
        public string? ResultUrl { get; set; }
    }

    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    public string Start(string owner, string kind, string title, Func<Context, Task<string>> work)
    {
        var job = new Job { Id = Guid.NewGuid().ToString("N")[..12], Kind = kind, Title = title, Owner = owner };
        _jobs[job.Id] = job;
        void Log(string line)
        {
            job.Log.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");
            while (job.Log.Count > 2000) job.Log.TryDequeue(out _);
        }
        _ = Task.Run(async () =>
        {
            var ctx = new Context(Log, job.Cancel.Token);
            try
            {
                job.Result = await work(ctx);
                job.ResultUrl = ctx.ResultUrl;
                job.State = "succeeded";
            }
            catch (OperationCanceledException) { job.State = "cancelled"; Log("Cancelled."); }
            catch (StudioException ex) { job.State = "failed"; job.Result = ex.Message; Log(ex.Message); }
            catch (Exception ex) { job.State = "failed"; job.Result = "Something unexpected went wrong: " + ex.Message; Log(job.Result); }
            finally { job.Finished = DateTime.UtcNow; }
        });
        return job.Id;
    }

    public JobStatus Get(string id) => _jobs.TryGetValue(id, out var j) ? ToStatus(j) : throw new StudioException("That task no longer exists.");

    public List<JobStatus> ForOwner(string owner) =>
        _jobs.Values.Where(j => string.Equals(j.Owner, owner, StringComparison.OrdinalIgnoreCase)).OrderByDescending(j => j.Started).Take(10).Select(ToStatus).ToList();

    public void Cancel(string id) { if (_jobs.TryGetValue(id, out var j)) j.Cancel.Cancel(); }

    private static JobStatus ToStatus(Job j) => new(j.Id, j.Kind, j.Title, j.State, j.Log.ToList(), j.Result, j.ResultUrl, j.Started, j.Finished);

    /// <summary>Runs a command and streams its output into the job log. Secrets in the arguments are masked in the log.</summary>
    public static async Task<int> RunAsync(Context ctx, string file, IEnumerable<string> args, string workDir, string[]? secrets = null, string? stdin = null, IDictionary<string, string>? env = null)
    {
        var list = args.ToList();
        var shown = string.Join(" ", list.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        foreach (var s in secrets ?? []) if (!string.IsNullOrEmpty(s)) shown = shown.Replace(s, "••••••");
        ctx.Log($"> {Path.GetFileNameWithoutExtension(file)} {shown}");
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in list) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        using var p = new Process { StartInfo = psi };
        void Line(string? l)
        {
            if (l is null) return;
            foreach (var s in secrets ?? []) if (!string.IsNullOrEmpty(s)) l = l.Replace(s, "••••••");
            ctx.Log(l);
        }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        try { p.Start(); }
        catch (Exception ex) { throw new StudioException($"Couldn't start {Path.GetFileName(file)}: {ex.Message}"); }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (stdin is not null) { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); }
        try { await p.WaitForExitAsync(ctx.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { /* ignore */ } throw; }
        return p.ExitCode;
    }
}
