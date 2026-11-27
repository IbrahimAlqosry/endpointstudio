using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using QosriStudio.Server.Models;

namespace QosriStudio.Server.Services;

public record TerminalStartBody(string Root, string? Shell, string? Command);
public record TerminalInputBody(string Text);
public record TerminalInfo(string Id, string Shell, string Directory, bool Running, int? ExitCode);
public record TerminalOutput(List<string> Chunks, long Next, bool Running, int? ExitCode);

/// <summary>Shell sessions inside a project folder: PowerShell, Command Prompt or Bash, with live output and typed input.</summary>
public partial class TerminalService
{
    private sealed class Session
    {
        public required string Id { get; init; }
        public required string Shell { get; init; }
        public required string Directory { get; init; }
        public required Process Process { get; init; }
        public List<string> Chunks { get; } = [];
        public long Offset { get; set; } // number of chunks dropped from the front
        public readonly Lock Gate = new();
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07]*\x07")]
    private static partial Regex AnsiRx();

    public static List<string> Shells()
    {
        var list = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            if (Which("pwsh.exe") is not null) list.Add("pwsh");
            list.Add("powershell");
            list.Add("cmd");
            if (GitBash() is not null) list.Add("bash");
        }
        else { list.Add("bash"); list.Add("sh"); }
        return list;
    }

    private static string? Which(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(d => d.Length > 0).Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);

    private static string? GitBash()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Programs" })
        {
            var p = Path.Combine(root, "Git", "bin", "bash.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public TerminalInfo Start(TerminalStartBody b)
    {
        var dir = ProjectFilesService.RootOf(b.Root);
        var shell = string.IsNullOrWhiteSpace(b.Shell) ? Shells()[0] : b.Shell;
        if (!Shells().Contains(shell)) throw new StudioException("That shell isn't available on this computer.");
        var (file, args) = shell switch
        {
            "pwsh" => ("pwsh.exe", new[] { "-NoLogo", "-NoProfile", "-NoExit", "-Command", "-" }),
            "powershell" => ("powershell.exe", new[] { "-NoLogo", "-NoProfile", "-NoExit", "-Command", "-" }),
            "cmd" => ("cmd.exe", new[] { "/Q", "/K" }),
            "bash" => (OperatingSystem.IsWindows() ? GitBash()! : "/bin/bash", new[] { "--login", "-i" }),
            _ => ("/bin/sh", new[] { "-i" }),
        };
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["TERM"] = "dumb";
        psi.Environment["FORCE_COLOR"] = "0";
        var proc = Process.Start(psi) ?? throw new StudioException("The terminal couldn't be started.");
        var session = new Session { Id = Guid.NewGuid().ToString("n")[..10], Shell = shell, Directory = dir, Process = proc };
        _sessions[session.Id] = session;
        _ = Pump(session, proc.StandardOutput);
        _ = Pump(session, proc.StandardError);
        if (shell is "powershell" or "pwsh")
        {
            // Pipes have no console: make PowerShell print a prompt after every command
            proc.StandardInput.WriteLine("[Console]::OutputEncoding = [Text.Encoding]::UTF8; function prompt { \"PS $($PWD.Path)> \" }; $ProgressPreference = 'SilentlyContinue'");
            proc.StandardInput.Flush();
        }
        if (!string.IsNullOrWhiteSpace(b.Command)) Input(session.Id, b.Command);
        Append(session, $"{Describe(shell)} in {dir}\n");
        return new TerminalInfo(session.Id, shell, dir, true, null);
    }

    private static string Describe(string shell) => shell switch { "pwsh" => "PowerShell 7", "powershell" => "Windows PowerShell", "cmd" => "Command Prompt", "bash" => "Bash", _ => shell };

    private static async Task Pump(Session s, StreamReader reader)
    {
        var buffer = new char[4096];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buffer)) > 0) Append(s, new string(buffer, 0, n));
        }
        catch { /* the process ended */ }
    }

    private static void Append(Session s, string text)
    {
        text = AnsiRx().Replace(text, "").Replace("\r\n", "\n");
        lock (s.Gate)
        {
            s.Chunks.Add(text);
            // Keep the last few thousand pieces
            if (s.Chunks.Count > 4000) { s.Chunks.RemoveRange(0, 1000); s.Offset += 1000; }
        }
    }

    private Session Get(string id) => _sessions.TryGetValue(id, out var s) ? s : throw new StudioException("That terminal has been closed.");

    public TerminalOutput Output(string id, long since)
    {
        var s = Get(id);
        s.LastSeen = DateTime.UtcNow;
        lock (s.Gate)
        {
            var start = (int)Math.Max(0, since - s.Offset);
            var chunks = start < s.Chunks.Count ? s.Chunks.GetRange(start, s.Chunks.Count - start) : [];
            var exited = s.Process.HasExited;
            return new TerminalOutput(chunks, s.Offset + s.Chunks.Count, !exited, exited ? s.Process.ExitCode : null);
        }
    }

    public void Input(string id, string text)
    {
        var s = Get(id);
        if (s.Process.HasExited) throw new StudioException("The terminal has ended. Open a new one.");
        // Echo what was typed, since there is no console to do it
        Append(s, text + "\n");
        s.Process.StandardInput.WriteLine(text);
        s.Process.StandardInput.Flush();
    }

    /// <summary>Stops whatever is running (and the shell). The session's output stays readable.</summary>
    public void Stop(string id)
    {
        var s = Get(id);
        try { if (!s.Process.HasExited) s.Process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        Append(s, "\n[stopped]\n");
    }

    public void Close(string id)
    {
        if (!_sessions.TryRemove(id, out var s)) return;
        try { if (!s.Process.HasExited) s.Process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        s.Process.Dispose();
    }

    public void CloseAll()
    {
        foreach (var id in _sessions.Keys.ToList()) Close(id);
    }
}
