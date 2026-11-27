using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using QosriStudio.Server.Models;

namespace QosriStudio.Server.Services;

public record TestCaseResult(string Name, string ClassName, string Outcome, double DurationMs, string? Message, string? StackTrace, string? Output, string? Project);
public record TestRunResult(int Total, int Passed, int Failed, int Skipped, double DurationMs, List<TestCaseResult> Tests, List<string> Projects);
public record TestRunBody(string Path, string? Filter, bool NoBuild = false);

/// <summary>Runs dotnet test (or npm test) and reads the results test by test.</summary>
public class TestService(JobService jobs)
{
    private readonly ConcurrentDictionary<string, TestRunResult> _results = new();

    public TestRunResult? Results(string jobId) => _results.GetValueOrDefault(jobId);

    public string Run(TestRunBody b)
    {
        var full = Path.GetFullPath(b.Path);
        var dir = File.Exists(full) ? Path.GetDirectoryName(full)! : full;
        if (full.EndsWith("package.json", StringComparison.OrdinalIgnoreCase))
            return jobs.Start(full, "test", "Running the app's tests", ctx => RunProcess(ctx, OperatingSystem.IsWindows() ? "npm.cmd" : "npm", ["test", "--", "--watch=false"], dir, null));

        var resultsDir = Path.Combine(Path.GetTempPath(), "QosriStudio", "tests", Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(resultsDir);
        var args = new List<string> { "test", full, "--logger", "trx", "--results-directory", resultsDir, "--nologo" };
        if (b.NoBuild) args.Add("--no-build");
        if (!string.IsNullOrWhiteSpace(b.Filter)) { args.Add("--filter"); args.Add(b.Filter); }
        string id = null!;
        id = jobs.Start(full, "test", string.IsNullOrWhiteSpace(b.Filter) ? "Running tests" : "Running selected tests", async ctx =>
        {
            int exit;
            try { exit = await RunExitCode(ctx, "dotnet", args, dir); }
            finally { }
            var parsed = Parse(resultsDir);
            _results[id] = parsed;
            try { Directory.Delete(resultsDir, true); } catch { /* ignore */ }
            if (parsed.Total == 0)
            {
                if (exit != 0) throw new StudioException("The tests couldn't run. The output above shows why (often a build error).");
                return "No tests were found. Test projects use xUnit, NUnit or MSTest and reference Microsoft.NET.Test.Sdk.";
            }
            if (parsed.Failed > 0) throw new StudioException($"{parsed.Failed} of {parsed.Total} tests failed.");
            return $"All {parsed.Passed} tests passed{(parsed.Skipped > 0 ? $" ({parsed.Skipped} skipped)" : "")}.";
        });
        return id;
    }

    private static async Task<string> RunProcess(JobService.Context ctx, string file, List<string> args, string dir, string? _)
    {
        var exit = await RunExitCode(ctx, file, args, dir);
        if (exit != 0) throw new StudioException("Some tests failed (or the tests couldn't run). See the output above.");
        return "The tests passed.";
    }

    private static async Task<int> RunExitCode(JobService.Context ctx, string file, List<string> args, string dir)
    {
        ctx.Log("> " + Path.GetFileNameWithoutExtension(file) + " " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        var psi = new ProcessStartInfo(file) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["CI"] = "true";
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) ctx.Log(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) ctx.Log(e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        using var reg = ctx.Token.Register(() => { try { p.Kill(true); } catch { } });
        await p.WaitForExitAsync(ctx.Token);
        return p.ExitCode;
    }

    /// <summary>Reads every .trx file (one per test project).</summary>
    private static TestRunResult Parse(string dir)
    {
        var tests = new List<TestCaseResult>();
        var projects = new List<string>();
        foreach (var file in Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.trx", SearchOption.AllDirectories) : [])
        {
            XDocument doc;
            try { doc = XDocument.Load(file); } catch { continue; }
            XNamespace ns = doc.Root?.Name.Namespace ?? "";
            var defs = doc.Descendants(ns + "UnitTest").ToDictionary(
                u => (string?)u.Attribute("id") ?? "",
                u => (Class: (string?)u.Element(ns + "TestMethod")?.Attribute("className") ?? "", Storage: Path.GetFileNameWithoutExtension((string?)u.Attribute("storage") ?? "")));
            foreach (var r in doc.Descendants(ns + "UnitTestResult"))
            {
                var testId = (string?)r.Attribute("testId") ?? "";
                var (cls, project) = defs.GetValueOrDefault(testId);
                if (!string.IsNullOrEmpty(project) && !projects.Contains(project)) projects.Add(project);
                var duration = TimeSpan.TryParse((string?)r.Attribute("duration"), out var d) ? d.TotalMilliseconds : 0;
                var err = r.Element(ns + "Output")?.Element(ns + "ErrorInfo");
                var outcome = ((string?)r.Attribute("outcome") ?? "Unknown") switch { "Passed" => "passed", "Failed" or "Error" or "Timeout" or "Aborted" => "failed", _ => "skipped" };
                var name = (string?)r.Attribute("testName") ?? "";
                if (cls.Length > 0 && name.StartsWith(cls + ".")) name = name[(cls.Length + 1)..];
                tests.Add(new TestCaseResult(name, cls, outcome, duration, err?.Element(ns + "Message")?.Value.Trim(), err?.Element(ns + "StackTrace")?.Value.Trim(),
                    r.Element(ns + "Output")?.Element(ns + "StdOut")?.Value.Trim(), project));
            }
        }
        return new TestRunResult(tests.Count, tests.Count(t => t.Outcome == "passed"), tests.Count(t => t.Outcome == "failed"), tests.Count(t => t.Outcome == "skipped"),
            tests.Sum(t => t.DurationMs), tests.OrderBy(t => t.Outcome == "failed" ? 0 : t.Outcome == "skipped" ? 2 : 1).ThenBy(t => t.ClassName).ThenBy(t => t.Name).ToList(), projects);
    }
}
