using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using QosriStudio.Server.Models;

namespace QosriStudio.Server.Services;

public record GitFile(string Path, string? OldPath, string Staged, string Unstaged, bool Untracked, bool Conflicted, bool InProject);
public record GitStatus(bool Installed, bool IsRepo, string? Root, string ProjectDir, string? Branch, string? Upstream, int Ahead, int Behind, bool Detached,
    List<GitFile> Files, List<GitRemote> Remotes, string? Operation, bool HasIdentity, string? UserName, string? UserEmail, bool HasGitignore, int Stashes, string? Problem);
public record GitRemote(string Name, string Url);
public record GitCommit(string Hash, string Short, string Author, string Email, DateTime Date, string Subject, List<string> Refs, bool Pushed);
public record GitCommitFile(string Path, string? OldPath, string Status, int Added, int Removed);
public record GitCommitDetails(GitCommit Commit, string Body, List<GitCommitFile> Files);
public record GitBranch(string Name, string Short, bool Remote, bool Current, string? Upstream, string? Track, DateTime? Date);
public record GitStash(string Ref, string Message, DateTime? Date);
public record GitDiff(string File, string Text, bool Binary, bool TooLarge);

public record GitPathBody(string Path);
public record GitFilesBody(string Path, List<string>? Files);
public record GitCommitBody(string Path, string Message, List<string>? Files, bool Amend = false);
public record GitNameBody(string Path, string Name, string? From = null, bool Force = false, bool Checkout = true);
public record GitRemoteBody(string Path, string Name, string Url);
public record GitIdentityBody(string Path, string Name, string Email, bool Global);
public record GitRefBody(string Path, string Ref, string? Message = null);
public record GitResolveBody(string Path, string File, string Keep);
public record GitCloneBody(string Url, string Folder);

/// <summary>Source control through the git command line: everything a team needs day to day.</summary>
public partial class GitService(JobService jobs)
{
    private static readonly Lazy<bool> GitInstalled = new(() =>
    {
        try { return Run(Environment.CurrentDirectory, ["--version"]).Exit == 0; } catch { return false; }
    });

    // ------------------------------------------------------------------ process

    private static (int Exit, string Out, string Err) Run(string dir, IEnumerable<string> args, string? input = null, int timeoutMs = 60_000)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-c", "core.quotepath=off", "-c", "color.ui=false" }.Concat(args)) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["LC_ALL"] = "C";
        using var p = Process.Start(psi) ?? throw new StudioException("Git couldn't be started.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (input is not null) { p.StandardInput.Write(input); p.StandardInput.Close(); }
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new StudioException("Git took too long and was stopped."); }
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    private static string Git(string dir, IEnumerable<string> args, string? input = null, bool allowFail = false)
    {
        var (exit, o, e) = Run(dir, args, input);
        if (exit != 0 && !allowFail) throw new StudioException(Friendly(e.Trim().Length > 0 ? e : o));
        return o;
    }

    private static string Friendly(string message)
    {
        var m = message.Trim();
        if (m.Contains("dubious ownership")) return "Git doesn't trust this folder because it belongs to another Windows user. Use “Trust this folder” to allow it.";
        if (m.Contains("Please tell me who you are") || m.Contains("unable to auto-detect email")) return "Git needs your name and email before it can save a version. Add them under “Your identity”.";
        if (m.Contains("Authentication failed") || m.Contains("could not read Username") || m.Contains("terminal prompts disabled"))
            return "The server asked for a sign-in. Sign in once with Git Credential Manager (for example by pushing from a terminal) or use an SSH address.";
        if (m.Contains("would be overwritten by")) return "You have unsaved changes to files this would replace. Save a version or put the changes aside (stash) first.";
        if (m.Contains("not fully merged")) return "This branch has work that isn't merged anywhere yet. Delete it anyway only if you're sure.";
        if (m.Contains("rejected") && m.Contains("fetch first") || m.Contains("non-fast-forward")) return "The server has newer changes. Get the latest changes (pull) first, then send yours.";
        if (m.Contains("CONFLICT")) return "Some files were changed on both sides. Choose which version to keep for each conflicted file, then save a version.";
        if (m.Contains("nothing to commit")) return "There's nothing to save: no files have changed.";
        if (m.Contains("does not have any commits yet") || m.Contains("ambiguous argument 'HEAD'")) return "This repository has no saved versions yet.";
        return Regex.Replace(m, @"^(error|fatal|hint):\s*", "", RegexOptions.Multiline);
    }

    // ------------------------------------------------------------------ locating

    private static string DirOf(string path)
    {
        var full = Path.GetFullPath(path);
        return File.Exists(full) ? Path.GetDirectoryName(full)! : Directory.Exists(full) ? full : throw new StudioException("That folder no longer exists.");
    }

    private static (string? Root, string? Problem) RootOf(string dir)
    {
        var (exit, o, e) = Run(dir, ["rev-parse", "--show-toplevel"]);
        if (exit == 0) return (Path.GetFullPath(o.Trim()), null);
        return (null, e.Contains("dubious ownership") ? Friendly(e) : null);
    }

    private static string RequireRoot(string path)
    {
        var (root, problem) = RootOf(DirOf(path));
        return root ?? throw new StudioException(problem ?? "This project isn't in a Git repository yet.");
    }

    private static void EnsureInstalled()
    {
        if (!GitInstalled.Value) throw new StudioException("Git isn't installed on this computer. Install it from git-scm.com and try again.");
    }

    // ------------------------------------------------------------------ status

    public GitStatus Status(string path)
    {
        var dir = DirOf(path);
        if (!GitInstalled.Value) return new GitStatus(false, false, null, dir, null, null, 0, 0, false, [], [], null, false, null, null, false, 0, null);
        var (root, problem) = RootOf(dir);
        if (root is null) return new GitStatus(true, false, null, dir, null, null, 0, 0, false, [], [], null, Identity(dir).Has, null, null, false, 0, problem);

        var raw = Git(root, ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"]);
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var files = new List<GitFile>();
        var rel = Path.GetRelativePath(root, dir).Replace('\\', '/');
        bool In(string p) => rel == "." || p.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase);
        var parts = raw.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var line = parts[i];
            if (line.Length == 0) continue;
            if (line.StartsWith("# branch.head ")) branch = line[14..];
            else if (line.StartsWith("# branch.upstream ")) upstream = line[18..];
            else if (line.StartsWith("# branch.ab "))
            {
                var m = Regex.Match(line, @"\+(\d+) -(\d+)");
                ahead = int.Parse(m.Groups[1].Value); behind = int.Parse(m.Groups[2].Value);
            }
            else if (line.StartsWith("1 "))
            {
                var f = line.Split(' ', 9);
                files.Add(new GitFile(f[8], null, Code(f[1][0]), Code(f[1][1]), false, false, In(f[8])));
            }
            else if (line.StartsWith("2 "))
            {
                var f = line.Split(' ', 10);
                var old = i + 1 < parts.Length ? parts[++i] : null;
                files.Add(new GitFile(f[9], old, Code(f[1][0]), Code(f[1][1]), false, false, In(f[9])));
            }
            else if (line.StartsWith("u "))
            {
                var f = line.Split(' ', 11);
                files.Add(new GitFile(f[10], null, "conflict", "conflict", false, true, In(f[10])));
            }
            else if (line.StartsWith("? ")) files.Add(new GitFile(line[2..], null, "", "added", true, false, In(line[2..])));
        }
        var gitDir = Path.GetFullPath(Path.Combine(root, Git(root, ["rev-parse", "--git-dir"]).Trim()));
        var op = File.Exists(Path.Combine(gitDir, "MERGE_HEAD")) ? "merge"
            : Directory.Exists(Path.Combine(gitDir, "rebase-merge")) || Directory.Exists(Path.Combine(gitDir, "rebase-apply")) ? "rebase"
            : File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")) ? "cherry-pick"
            : File.Exists(Path.Combine(gitDir, "REVERT_HEAD")) ? "revert" : null;
        var id = Identity(root);
        var stashes = Git(root, ["stash", "list"], allowFail: true).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        return new GitStatus(true, true, root, dir, branch == "(detached)" ? null : branch, upstream, ahead, behind, branch == "(detached)",
            files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(), Remotes(root), op, id.Has, id.Name, id.Email,
            File.Exists(Path.Combine(root, ".gitignore")), stashes, null);
    }

    private static string Code(char c) => c switch
    {
        'M' or 'T' => "modified",
        'A' => "added",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        _ => "",
    };

    private static (bool Has, string? Name, string? Email) Identity(string dir)
    {
        var name = Run(dir, ["config", "user.name"]).Out.Trim();
        var email = Run(dir, ["config", "user.email"]).Out.Trim();
        return (name.Length > 0 && email.Length > 0, name.Length > 0 ? name : null, email.Length > 0 ? email : null);
    }

    private static List<GitRemote> Remotes(string root) =>
        Git(root, ["remote", "-v"], allowFail: true).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t', ' ')).Where(p => p.Length >= 2).Select(p => new GitRemote(p[0], p[1])).DistinctBy(r => r.Name).ToList();

    // ------------------------------------------------------------------ setup

    public GitStatus Init(string path)
    {
        EnsureInstalled();
        var dir = DirOf(path);
        // Put the repository at the solution / app folder, not inside a single project of a solution
        var target = FindRepoHome(dir);
        Git(target, ["init", "-b", "main"]);
        var ignore = Path.Combine(target, ".gitignore");
        if (!File.Exists(ignore)) File.WriteAllText(ignore, DefaultIgnore);
        return Status(path);
    }

    private static string FindRepoHome(string dir)
    {
        var d = new DirectoryInfo(dir);
        for (var cur = d; cur is not null && cur.Parent is not null; cur = cur.Parent)
        {
            if (cur.GetFiles("*.sln").Length + cur.GetFiles("*.slnx").Length > 0) return cur.FullName;
            if (cur != d) break;
        }
        // A project inside a folder that has the solution one level up
        var up = d.Parent;
        if (up is not null && up.GetFiles("*.sln").Length + up.GetFiles("*.slnx").Length > 0) return up.FullName;
        return dir;
    }

    public void AddGitignore(string path)
    {
        var root = RequireRoot(path);
        var file = Path.Combine(root, ".gitignore");
        if (File.Exists(file))
        {
            var existing = File.ReadAllText(file);
            var missing = DefaultIgnore.Split('\n').Where(l => l.Trim().Length > 0 && !l.StartsWith('#') && !existing.Contains(l.Trim())).ToList();
            if (missing.Count > 0) File.AppendAllText(file, "\n# Added by QosriStudio\n" + string.Join("\n", missing) + "\n");
        }
        else File.WriteAllText(file, DefaultIgnore);
    }

    private const string DefaultIgnore = """
        # Build output
        bin/
        obj/
        out/
        publish/
        dist/
        build/
        .angular/
        .next/
        coverage/

        # Dependencies
        node_modules/
        packages/

        # Editors and tools
        .vs/
        .vscode/
        .idea/
        *.user
        *.suo
        *.swp
        .DS_Store
        Thumbs.db

        # Logs and local data
        *.log
        logs/
        *.db-shm
        *.db-wal

        # Secrets that must never be shared
        .env
        .env.local
        .env.*.local
        appsettings.*.local.json
        *.pfx
        *.pem
        secrets.json

        """;

    public void SetIdentity(GitIdentityBody b)
    {
        EnsureInstalled();
        if (string.IsNullOrWhiteSpace(b.Name) || !b.Email.Contains('@')) throw new StudioException("Enter your name and a valid email address.");
        var dir = b.Global ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : RequireRoot(b.Path);
        var scope = b.Global ? "--global" : "--local";
        Git(dir, ["config", scope, "user.name", b.Name.Trim()]);
        Git(dir, ["config", scope, "user.email", b.Email.Trim()]);
    }

    public void Trust(string path)
    {
        EnsureInstalled();
        var dir = DirOf(path);
        var top = dir;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) { top = d.FullName; break; }
        Git(Environment.CurrentDirectory, ["config", "--global", "--add", "safe.directory", top.Replace('\\', '/')]);
    }

    public void SetRemote(GitRemoteBody b)
    {
        var root = RequireRoot(b.Path);
        var name = string.IsNullOrWhiteSpace(b.Name) ? "origin" : b.Name.Trim();
        if (!Regex.IsMatch(b.Url.Trim(), @"^(https?://|git@|ssh://|file://|[A-Za-z]:[\\/]|\\\\|/)")) throw new StudioException("That doesn't look like a repository address.");
        var exists = Remotes(root).Any(r => r.Name == name);
        Git(root, exists ? ["remote", "set-url", name, b.Url.Trim()] : ["remote", "add", name, b.Url.Trim()]);
    }

    public void RemoveRemote(string path, string name) => Git(RequireRoot(path), ["remote", "remove", name]);

    // ------------------------------------------------------------------ changes

    private static string PathSpec(IEnumerable<string> files) => string.Join('\0', files) + '\0';

    public void Stage(GitFilesBody b)
    {
        var root = RequireRoot(b.Path);
        if (b.Files is { Count: > 0 }) Git(root, ["add", "-A", "--pathspec-from-file=-", "--pathspec-file-nul"], PathSpec(b.Files));
        else Git(root, ["add", "-A"]);
    }

    public void Unstage(GitFilesBody b)
    {
        var root = RequireRoot(b.Path);
        var hasHead = Run(root, ["rev-parse", "--verify", "HEAD"]).Exit == 0;
        if (!hasHead) { Git(root, b.Files is { Count: > 0 } ? ["rm", "--cached", "-r", "-q", "--pathspec-from-file=-", "--pathspec-file-nul"] : ["rm", "--cached", "-r", "-q", "."], b.Files is { Count: > 0 } ? PathSpec(b.Files) : null); return; }
        if (b.Files is { Count: > 0 }) Git(root, ["restore", "--staged", "--pathspec-from-file=-", "--pathspec-file-nul"], PathSpec(b.Files));
        else Git(root, ["restore", "--staged", "."]);
    }

    public object Commit(GitCommitBody b)
    {
        var root = RequireRoot(b.Path);
        if (!Identity(root).Has) throw new StudioException("Git needs your name and email before it can save a version. Add them under “Your identity”.");
        var status = Status(b.Path);
        var inMerge = status.Operation is "merge" or "cherry-pick" or "revert";
        if (string.IsNullOrWhiteSpace(b.Message) && !b.Amend && !inMerge) throw new StudioException("Describe what changed, for example “Add orders endpoint”.");
        if (status.Files.Any(f => f.Conflicted)) throw new StudioException("Some files still have conflicts. Choose which version to keep for each one first.");
        var args = new List<string> { "commit" };
        if (b.Amend) args.Add("--amend");
        if (!string.IsNullOrWhiteSpace(b.Message)) { args.Add("-m"); args.Add(b.Message.Trim()); } else args.Add("--no-edit");
        if (b.Files is { Count: > 0 } && !inMerge)
        {
            Git(root, ["add", "-A", "--pathspec-from-file=-", "--pathspec-file-nul"], PathSpec(b.Files));
            Git(root, [.. args, "--pathspec-from-file=-", "--pathspec-file-nul"], PathSpec(b.Files));
        }
        else
        {
            if (b.Files is null && !inMerge) Git(root, ["add", "-A"]);
            Git(root, args);
        }
        var hash = Git(root, ["rev-parse", "--short", "HEAD"]).Trim();
        return new { hash };
    }

    public void Discard(GitFilesBody b)
    {
        var root = RequireRoot(b.Path);
        var status = Status(b.Path);
        var files = b.Files is { Count: > 0 } ? status.Files.Where(f => b.Files.Contains(f.Path)).ToList() : status.Files;
        var untracked = files.Where(f => f.Untracked).Select(f => f.Path).ToList();
        var tracked = files.Where(f => !f.Untracked).SelectMany(f => f.OldPath is null ? [f.Path] : new[] { f.Path, f.OldPath }).ToList();
        var hasHead = Run(root, ["rev-parse", "--verify", "HEAD"]).Exit == 0;
        if (tracked.Count > 0)
        {
            if (hasHead) Git(root, ["restore", "--source=HEAD", "--staged", "--worktree", "--pathspec-from-file=-", "--pathspec-file-nul"], PathSpec(tracked));
            else { Git(root, ["rm", "--cached", "-r", "-q", "--pathspec-from-file=-", "--pathspec-file-nul"], PathSpec(tracked)); untracked.AddRange(tracked); }
        }
        foreach (var f in untracked)
        {
            var full = Path.GetFullPath(Path.Combine(root, f));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(full)) File.Delete(full);
        }
    }

    public GitDiff Diff(string path, string file, string? commit)
    {
        var root = RequireRoot(path);
        string text;
        if (commit is not null) text = Git(root, ["show", "--format=", "--no-color", "-M", commit, "--", file], allowFail: true);
        else
        {
            var tracked = Run(root, ["ls-files", "--error-unmatch", "--", file]).Exit == 0;
            var hasHead = Run(root, ["rev-parse", "--verify", "HEAD"]).Exit == 0;
            text = tracked && hasHead ? Git(root, ["diff", "HEAD", "--no-color", "-M", "--", file], allowFail: true)
                : Run(root, ["diff", "--no-index", "--no-color", "--", OperatingSystem.IsWindows() ? "NUL" : "/dev/null", file]).Out;
        }
        var binary = text.Contains("Binary files ");
        var tooLarge = text.Length > 600_000;
        return new GitDiff(file, tooLarge ? text[..600_000] : text, binary, tooLarge);
    }

    public void Resolve(GitResolveBody b)
    {
        var root = RequireRoot(b.Path);
        if (b.Keep is "mine" or "theirs") Git(root, ["checkout", b.Keep == "mine" ? "--ours" : "--theirs", "--", b.File]);
        Git(root, ["add", "--", b.File]);
    }

    public void AbortOperation(string path)
    {
        var root = RequireRoot(path);
        var op = Status(path).Operation ?? throw new StudioException("Nothing is in progress.");
        Git(root, [op, "--abort"]);
    }

    // ------------------------------------------------------------------ history

    private const string LogFormat = "%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%s%x1f%D%x1e";

    public List<GitCommit> Log(string path, int skip, string? branch, string? search, string? file)
    {
        var root = RequireRoot(path);
        if (Run(root, ["rev-parse", "--verify", "HEAD"]).Exit != 0) return [];
        var args = new List<string> { "log", $"--format={LogFormat}", "-n", "50", $"--skip={Math.Max(0, skip)}" };
        if (!string.IsNullOrWhiteSpace(search)) { args.Add($"--grep={search}"); args.Add("-i"); }
        args.Add(string.IsNullOrWhiteSpace(branch) ? "HEAD" : branch);
        if (!string.IsNullOrWhiteSpace(file)) { args.Add("--"); args.Add(file); }
        var unpushed = UnpushedHashes(root);
        return Git(root, args).Split('\x1e', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim('\n', '\r'))
            .Where(s => s.Length > 0).Select(s => ParseCommit(s, unpushed)).ToList();
    }

    private static HashSet<string> UnpushedHashes(string root)
    {
        var (exit, o, _) = Run(root, ["rev-list", "HEAD", "--not", "--remotes", "-n", "500"]);
        return exit == 0 ? o.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet() : [];
    }

    private static GitCommit ParseCommit(string s, HashSet<string> unpushed)
    {
        var p = s.Split('\x1f');
        var refs = p.Length > 6 && p[6].Length > 0 ? p[6].Split(", ").Select(r => r.Replace("HEAD -> ", "")).Where(r => r != "HEAD" && !r.EndsWith("/HEAD")).ToList() : [];
        return new GitCommit(p[0], p[1], p[2], p[3], DateTime.Parse(p[4]).ToUniversalTime(), p[5], refs, !unpushed.Contains(p[0]));
    }

    public GitCommitDetails Show(string path, string hash)
    {
        var root = RequireRoot(path);
        var head = Git(root, ["show", "-s", $"--format={LogFormat}%B", hash]);
        var parts = head.Split('\x1e', 2);
        var commit = ParseCommit(parts[0], UnpushedHashes(root));
        var body = parts.Length > 1 ? string.Join('\n', parts[1].Trim().Split('\n').Skip(1)).Trim() : "";
        var stats = new Dictionary<string, (int, int)>();
        foreach (var l in Git(root, ["show", "--format=", "--numstat", "-M", hash]).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = l.Split('\t');
            if (f.Length < 3) continue;
            var name = f[2].Contains(" => ") ? Regex.Replace(f[2], @"\{?([^{}]*) => ([^{}]*)\}?", "$2") : f[2];
            stats[name] = (int.TryParse(f[0], out var a) ? a : 0, int.TryParse(f[1], out var r) ? r : 0);
        }
        var files = new List<GitCommitFile>();
        var ns = Git(root, ["show", "--format=", "--name-status", "-M", "-z", hash]).Split('\0');
        for (var i = 0; i < ns.Length; i++)
        {
            var code = ns[i];
            if (code.Length == 0) continue;
            string? old = null;
            if (code[0] is 'R' or 'C') old = ns[++i];
            var file = ns[++i];
            var st = stats.GetValueOrDefault(file);
            files.Add(new GitCommitFile(file, old, Code(code[0]) is { Length: > 0 } c ? c : "modified", st.Item1, st.Item2));
        }
        return new GitCommitDetails(commit, body, files);
    }

    public void Revert(GitRefBody b)
    {
        var root = RequireRoot(b.Path);
        var parents = Git(root, ["rev-list", "--parents", "-n", "1", b.Ref]).Trim().Split(' ').Length - 1;
        Git(root, parents > 1 ? ["revert", "--no-edit", "-m", "1", b.Ref] : ["revert", "--no-edit", b.Ref]);
    }

    public void UndoLastCommit(string path)
    {
        var root = RequireRoot(path);
        var head = Git(root, ["rev-parse", "HEAD"]).Trim();
        if (!UnpushedHashes(root).Contains(head)) throw new StudioException("The last version was already sent to the server. Use “Undo these changes” on it instead, which is safe for everyone else.");
        if (Run(root, ["rev-parse", "--verify", "HEAD~1"]).Exit != 0) throw new StudioException("This is the first version of the repository, so there is nothing before it.");
        Git(root, ["reset", "--soft", "HEAD~1"]);
    }

    public void Tag(GitRefBody b)
    {
        var root = RequireRoot(b.Path);
        var name = (b.Message ?? "").Trim();
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9._/-]+$")) throw new StudioException("Use letters, numbers, dots or dashes for the label, e.g. v1.2.0.");
        Git(root, ["tag", "-a", name, "-m", name, b.Ref]);
    }

    // ------------------------------------------------------------------ branches

    public List<GitBranch> Branches(string path)
    {
        var root = RequireRoot(path);
        var o = Git(root, ["for-each-ref", "--format=%(refname)%1f%(refname:short)%1f%(HEAD)%1f%(upstream:short)%1f%(upstream:track)%1f%(committerdate:iso-strict)", "refs/heads", "refs/remotes"]);
        return o.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\x1f')).Where(p => p.Length >= 6 && !p[0].EndsWith("/HEAD"))
            .Select(p => new GitBranch(p[1], p[0].StartsWith("refs/remotes/") ? p[1][(p[1].IndexOf('/') + 1)..] : p[1], p[0].StartsWith("refs/remotes/"), p[2] == "*",
                p[3].Length > 0 ? p[3] : null, p[4].Trim('[', ']') is { Length: > 0 } t ? t : null, DateTime.TryParse(p[5], out var d) ? d.ToUniversalTime() : null))
            .OrderByDescending(b => b.Current).ThenBy(b => b.Remote).ThenByDescending(b => b.Date).ToList();
    }

    private static void ValidBranch(string root, string name)
    {
        if (Run(root, ["check-ref-format", "--branch", name]).Exit != 0) throw new StudioException("That isn't a valid branch name. Use letters, numbers, dashes and slashes, e.g. feature/orders.");
    }

    public void CreateBranch(GitNameBody b)
    {
        var root = RequireRoot(b.Path);
        ValidBranch(root, b.Name);
        var from = string.IsNullOrWhiteSpace(b.From) ? "HEAD" : b.From;
        Git(root, b.Checkout ? ["switch", "-c", b.Name, from] : ["branch", b.Name, from]);
    }

    public void Checkout(GitNameBody b)
    {
        var root = RequireRoot(b.Path);
        // A branch that only exists on the server becomes a local branch that follows it
        var remote = Branches(b.Path).FirstOrDefault(x => x.Remote && x.Name == b.Name);
        if (remote is not null && !Branches(b.Path).Any(x => !x.Remote && x.Name == remote.Short)) Git(root, ["switch", "--track", b.Name]);
        else Git(root, ["switch", remote?.Short ?? b.Name]);
    }

    public void DeleteBranch(GitNameBody b)
    {
        var root = RequireRoot(b.Path);
        var branch = Branches(b.Path).FirstOrDefault(x => x.Name == b.Name) ?? throw new StudioException("That branch no longer exists.");
        if (branch.Current) throw new StudioException("Switch to another branch before deleting this one.");
        if (branch.Remote)
        {
            var slash = b.Name.IndexOf('/');
            Git(root, ["push", b.Name[..slash], "--delete", b.Name[(slash + 1)..]]);
        }
        else Git(root, ["branch", b.Force ? "-D" : "-d", b.Name]);
    }

    public void RenameBranch(GitNameBody b)
    {
        var root = RequireRoot(b.Path);
        ValidBranch(root, b.Name);
        Git(root, ["branch", "-m", b.From ?? throw new StudioException("Which branch?"), b.Name]);
    }

    public object Merge(GitNameBody b)
    {
        var root = RequireRoot(b.Path);
        var (exit, o, e) = Run(root, ["merge", "--no-edit", b.Name], timeoutMs: 120_000);
        if (exit == 0) return new { conflicts = false, message = o.Contains("Already up to date") ? "Already up to date." : $"{b.Name} was merged in." };
        if ((o + e).Contains("CONFLICT")) return new { conflicts = true, message = "Some files were changed on both branches. Choose which version to keep for each one." };
        throw new StudioException(Friendly(e.Length > 0 ? e : o));
    }

    // ------------------------------------------------------------------ stash

    public List<GitStash> Stashes(string path)
    {
        var root = RequireRoot(path);
        return Git(root, ["stash", "list", "--format=%gd%x1f%s%x1f%cI"], allowFail: true).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\x1f')).Where(p => p.Length >= 3).Select(p => new GitStash(p[0], Regex.Replace(p[1], @"^On [^:]+: ", ""), DateTime.TryParse(p[2], out var d) ? d.ToUniversalTime() : null)).ToList();
    }

    public void Stash(GitRefBody b)
    {
        var root = RequireRoot(b.Path);
        var msg = string.IsNullOrWhiteSpace(b.Message) ? $"Set aside {DateTime.Now:g}" : b.Message.Trim();
        Git(root, ["stash", "push", "--include-untracked", "-m", msg]);
    }

    public void StashApply(GitRefBody b, bool drop)
    {
        var root = RequireRoot(b.Path);
        if (!Regex.IsMatch(b.Ref, @"^stash@\{\d+\}$")) throw new StudioException("Unknown stash.");
        Git(root, ["stash", drop ? "pop" : "apply", b.Ref]);
    }

    public void StashDrop(GitRefBody b)
    {
        var root = RequireRoot(b.Path);
        if (!Regex.IsMatch(b.Ref, @"^stash@\{\d+\}$")) throw new StudioException("Unknown stash.");
        Git(root, ["stash", "drop", b.Ref]);
    }

    // ------------------------------------------------------------------ server (long-running, shown live)

    private string StartGitJob(string owner, string kind, string title, string dir, List<string> args, Func<string>? done = null) =>
        jobs.Start(owner, kind, title, async ctx =>
        {
            ctx.Log("> git " + string.Join(' ', args));
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-c", "color.ui=false" }.Concat(args)) psi.ArgumentList.Add(a);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var p = Process.Start(psi)!;
            var err = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) ctx.Log(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) { ctx.Log(e.Data); err.AppendLine(e.Data); } };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            using var reg = ctx.Token.Register(() => { try { p.Kill(true); } catch { } });
            await p.WaitForExitAsync(ctx.Token);
            if (p.ExitCode != 0)
            {
                var all = err.ToString();
                if (all.Contains("CONFLICT")) return "Some files were changed on both sides. Open Source control and choose which version to keep.";
                throw new StudioException(Friendly(all));
            }
            return done?.Invoke() ?? "Done.";
        });

    public string Fetch(string path) => StartGitJob(RequireRoot(path), "git", "Checking the server for changes", RequireRoot(path), ["fetch", "--all", "--prune", "--progress"]);

    public string Pull(string path)
    {
        var root = RequireRoot(path);
        var s = Status(path);
        if (s.Upstream is null) throw new StudioException(s.Remotes.Count == 0 ? "This repository isn't connected to a server yet. Add a server address first." : "This branch isn't on the server yet. Send it first (push).");
        return StartGitJob(root, "git", "Getting the latest changes", root, ["pull", "--no-rebase", "--no-edit", "--progress"]);
    }

    public string Push(string path, bool tags)
    {
        var root = RequireRoot(path);
        var s = Status(path);
        if (s.Remotes.Count == 0) throw new StudioException("This repository isn't connected to a server yet. Add a server address first.");
        if (s.Branch is null) throw new StudioException("You're looking at an old version, not a branch. Switch to a branch first.");
        var args = s.Upstream is null ? new List<string> { "push", "--progress", "-u", s.Remotes.FirstOrDefault(r => r.Name == "origin")?.Name ?? s.Remotes[0].Name, s.Branch } : ["push", "--progress"];
        if (tags) args.Add("--follow-tags");
        return StartGitJob(root, "git", $"Sending {s.Branch} to the server", root, args);
    }

    public string Clone(GitCloneBody b)
    {
        EnsureInstalled();
        var url = b.Url.Trim();
        if (!Regex.IsMatch(url, @"^(https?://|git@|ssh://|file://)")) throw new StudioException("Paste the repository address, e.g. https://github.com/team/app.git.");
        var parent = Path.GetFullPath(b.Folder.Trim());
        if (!Directory.Exists(parent)) throw new StudioException("Choose an existing folder to put the copy in.");
        var name = Regex.Replace(url.TrimEnd('/'), @"^.*[/:]", "");
        if (name.EndsWith(".git")) name = name[..^4];
        var target = Path.Combine(parent, name);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new StudioException($"The folder {name} already exists there and isn't empty.");
        return StartGitJob("clone", "clone", $"Copying {name}", parent, ["clone", "--progress", url, target], () => target);
    }
}
