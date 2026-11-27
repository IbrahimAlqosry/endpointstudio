using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QosriStudio.Server.Models;

namespace QosriStudio.Server.Services;

public record AgentTool(string Id, string Name, bool Installed, string? Path, string? Note);
public record AgentRunBody(string Root, string Tool, string Prompt, string Mode, string? Model, string? SessionId);
public record AgentEvent(int Seq, string Kind, string Text, string? Tool, DateTime Time);
public record AgentStatus(string Id, string Tool, bool Running, string? SessionId, List<AgentEvent> Events, int Next, double? CostUsd, string? Result, bool Failed);

/// <summary>Runs AI coding agents (Claude Code, Cursor's agent) inside a project folder and streams what they do.</summary>
public class AgentService
{
    private sealed class Run
    {
        public required string Id { get; init; }
        public required string Tool { get; init; }
        public required Process Process { get; init; }
        public List<AgentEvent> Events { get; } = [];
        public string? SessionId { get; set; }
        public double? Cost { get; set; }
        public string? Result { get; set; }
        public bool Failed { get; set; }
        public readonly Lock Gate = new();
    }

    private readonly ConcurrentDictionary<string, Run> _runs = new();

    private static string? Find(params string[] names)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(d => d.Length > 0).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.Add(Path.Combine(home, ".local", "bin"));
        dirs.Add(Path.Combine(home, ".cursor", "bin"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var d in dirs)
            foreach (var n in names)
                foreach (var ext in OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", "" } : [""])
                {
                    var p = Path.Combine(d, n + ext);
                    if (File.Exists(p)) return p;
                }
        return null;
    }

    public static List<AgentTool> Tools()
    {
        var claude = Find("claude");
        var cursorAgent = Find("cursor-agent", "agent");
        var cursor = Find("cursor");
        return
        [
            new AgentTool("claude", "Claude Code", claude is not null, claude, claude is null ? "Install Claude Code from claude.com/code, then sign in once by running “claude” in a terminal." : null),
            new AgentTool("cursor", "Cursor agent", cursorAgent is not null, cursorAgent, cursorAgent is null ? (cursor is not null ? "The Cursor editor is installed, but not its command-line agent (cursor-agent). Install it from cursor.com/cli to use it here, or open the project in Cursor." : "Install Cursor and its command-line agent from cursor.com/cli.") : null),
            new AgentTool("cursor-editor", "Cursor editor", cursor is not null, cursor, null),
        ];
    }

    public AgentStatus Start(AgentRunBody b)
    {
        var dir = ProjectFilesService.RootOf(b.Root);
        if (string.IsNullOrWhiteSpace(b.Prompt)) throw new StudioException("Describe what the agent should do.");
        var tool = Tools().FirstOrDefault(t => t.Id == b.Tool) ?? throw new StudioException("Unknown agent.");
        if (!tool.Installed || tool.Path is null) throw new StudioException(tool.Note ?? $"{tool.Name} isn't installed.");

        var psi = new ProcessStartInfo(tool.Path)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var args = new List<string>();
        if (b.Tool == "claude")
        {
            args.AddRange(["-p", b.Prompt, "--output-format", "stream-json", "--verbose"]);
            switch (b.Mode)
            {
                case "plan": args.AddRange(["--permission-mode", "plan"]); break;
                case "build":
                    args.AddRange(["--permission-mode", "acceptEdits", "--allowedTools",
                        "Bash(dotnet build:*)", "Bash(dotnet test:*)", "Bash(dotnet restore:*)", "Bash(dotnet format:*)", "Bash(npm run:*)", "Bash(npm test:*)", "Bash(npx tsc:*)",
                        "Bash(git status:*)", "Bash(git diff:*)", "Bash(git log:*)"]);
                    break;
                case "auto": args.AddRange(["--permission-mode", "auto"]); break;
                default: args.AddRange(["--permission-mode", "acceptEdits"]); break;
            }
            if (!string.IsNullOrWhiteSpace(b.Model)) args.AddRange(["--model", b.Model]);
            if (!string.IsNullOrWhiteSpace(b.SessionId)) args.AddRange(["--resume", b.SessionId]);
        }
        else
        {
            args.AddRange(["-p", b.Prompt, "--output-format", "stream-json"]);
            if (b.Mode is "build" or "auto") args.Add("--force");
            if (!string.IsNullOrWhiteSpace(b.Model)) args.AddRange(["--model", b.Model]);
            if (!string.IsNullOrWhiteSpace(b.SessionId)) args.AddRange(["--resume", b.SessionId]);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["NO_COLOR"] = "1";
        var proc = Process.Start(psi) ?? throw new StudioException($"{tool.Name} couldn't be started.");
        proc.StandardInput.Close(); // no interactive questions: everything comes from the prompt
        var run = new Run { Id = Guid.NewGuid().ToString("n")[..10], Tool = b.Tool, Process = proc, SessionId = b.SessionId };
        _runs[run.Id] = run;
        Add(run, "prompt", b.Prompt, null);
        _ = ReadOut(run, proc.StandardOutput);
        _ = ReadErr(run, proc.StandardError);
        _ = proc.WaitForExitAsync().ContinueWith(_ =>
        {
            if (proc.ExitCode != 0 && run.Result is null) { run.Failed = true; Add(run, "error", $"The agent stopped (exit code {proc.ExitCode}).", null); }
            Add(run, "done", run.Failed ? "Stopped." : "Finished.", null);
        });
        return Status(run.Id, 0);
    }

    private static void Add(Run r, string kind, string text, string? tool)
    {
        lock (r.Gate) r.Events.Add(new AgentEvent(r.Events.Count, kind, text, tool, DateTime.UtcNow));
    }

    private static async Task ReadErr(Run r, StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
            if (line.Trim().Length > 0) Add(r, "log", line, null);
    }

    /// <summary>Turns the agent's JSON stream into readable steps.</summary>
    private static async Task ReadOut(Run r, StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (line.Trim().Length == 0) continue;
            JsonNode? n;
            try { n = JsonNode.Parse(line); } catch { Add(r, "log", line, null); continue; }
            var type = n?["type"]?.ToString();
            if (n?["session_id"]?.ToString() is { Length: > 0 } sid) r.SessionId = sid;
            switch (type)
            {
                case "system":
                    if (n?["subtype"]?.ToString() == "init") Add(r, "info", $"Started ({n?["model"]?.ToString() ?? "default model"}) in {n?["cwd"]}", null);
                    break;
                case "assistant":
                    foreach (var c in n?["message"]?["content"]?.AsArray() ?? [])
                    {
                        var ct = c?["type"]?.ToString();
                        if (ct == "text" && c?["text"]?.ToString() is { Length: > 0 } t) Add(r, "text", t, null);
                        else if (ct == "tool_use") Add(r, "tool", Describe(c?["name"]?.ToString() ?? "tool", c?["input"]), c?["name"]?.ToString());
                    }
                    break;
                case "user":
                    foreach (var c in n?["message"]?["content"]?.AsArray() ?? [])
                        if (c?["type"]?.ToString() == "tool_result" && c?["is_error"]?.GetValue<bool>() == true)
                            Add(r, "warn", Trim(c?["content"] is JsonArray arr ? string.Concat(arr.Select(x => x?["text"]?.ToString())) : c?["content"]?.ToString() ?? ""), null);
                    break;
                case "result":
                    r.Result = n?["result"]?.ToString();
                    r.Cost = n?["total_cost_usd"]?.GetValue<double?>();
                    r.Failed = n?["is_error"]?.GetValue<bool>() == true;
                    var turns = n?["num_turns"]?.ToString();
                    var secs = n?["duration_ms"]?.GetValue<double?>() / 1000;
                    Add(r, r.Failed ? "error" : "result", r.Result ?? (r.Failed ? "The agent couldn't finish." : "Done."), $"{turns} steps · {secs:0}s");
                    break;
                default:
                    // Cursor's agent uses a similar stream; show anything readable
                    if (n?["text"]?.ToString() is { Length: > 0 } text) Add(r, "text", text, null);
                    break;
            }
        }
    }

    private static string Describe(string tool, JsonNode? input)
    {
        string? S(string k) => input?[k]?.ToString();
        return tool switch
        {
            "Read" => $"Reading {Short(S("file_path"))}",
            "Edit" or "MultiEdit" => $"Editing {Short(S("file_path"))}",
            "Write" => $"Writing {Short(S("file_path"))}",
            "Bash" => $"Running: {S("command")}",
            "Grep" => $"Searching for “{S("pattern")}”",
            "Glob" => $"Finding files {S("pattern")}",
            "TodoWrite" => "Planning the steps",
            "WebFetch" or "WebSearch" => $"Looking up {S("url") ?? S("query")}",
            "Task" or "Agent" => $"Delegating: {S("description")}",
            _ => tool,
        };
    }

    private static string Short(string? path) => path is null ? "a file" : string.Join('/', path.Replace('\\', '/').Split('/').TakeLast(3));
    private static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s;

    public AgentStatus Status(string id, int since)
    {
        var r = _runs.TryGetValue(id, out var x) ? x : throw new StudioException("That agent run is no longer available.");
        lock (r.Gate)
            return new AgentStatus(r.Id, r.Tool, !r.Process.HasExited, r.SessionId, r.Events.Skip(since).ToList(), r.Events.Count, r.Cost, r.Result, r.Failed);
    }

    public void Stop(string id)
    {
        if (!_runs.TryGetValue(id, out var r)) return;
        try { if (!r.Process.HasExited) r.Process.Kill(entireProcessTree: true); } catch { /* gone */ }
        Add(r, "warn", "Stopped by you.", null);
    }

    public void StopAll()
    {
        foreach (var id in _runs.Keys) Stop(id);
    }

    /// <summary>Opens the folder (and optionally a file at a line) in the Cursor editor.</summary>
    public static void OpenInCursor(string root, string? file, int? line)
    {
        var cursor = Tools().First(t => t.Id == "cursor-editor");
        if (!cursor.Installed) throw new StudioException("Cursor isn't installed. Get it from cursor.com.");
        var dir = ProjectFilesService.RootOf(root);
        var psi = new ProcessStartInfo(cursor.Path!) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(dir);
        if (!string.IsNullOrEmpty(file)) { psi.ArgumentList.Add("-g"); psi.ArgumentList.Add(Path.Combine(dir, file) + (line is > 0 ? $":{line}" : "")); }
        Process.Start(psi);
    }
}
