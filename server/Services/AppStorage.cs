using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QosriStudio.Server.Models;

namespace QosriStudio.Server.Services;

/// <summary>Location for the studio's own data (recent projects, change history).</summary>
public static class AppStorage
{
    public static readonly string Root = ResolveRoot();

    /// <summary>
    /// %LOCALAPPDATA%\QosriStudio. The app used to be called Stackwright and, before that, Endpoint Studio; the newest
    /// old folder (recent list, history, saved connections, publish options) is moved over the first time so nothing is lost.
    /// </summary>
    private static string ResolveRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(local, "QosriStudio");
        try
        {
            if (!Directory.Exists(root))
            {
                var old = new[] { "Stackwright", "EndpointStudio" }.Select(n => Path.Combine(local, n)).FirstOrDefault(Directory.Exists);
                if (old is not null) Directory.Move(old, root);
            }
        }
        catch { /* the old folder stays where it is; a fresh one is used */ }
        return root;
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string ProjectKey(string projectPath)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(projectPath).ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16];
    }
}

public class RecentProjectsStore
{
    private readonly string _file = Path.Combine(AppStorage.Root, "recent.json");
    private readonly Lock _lock = new();

    public List<RecentProject> GetAll()
    {
        lock (_lock)
        {
            return Load().Where(p => File.Exists(p.Path)).OrderByDescending(p => p.LastOpened).ToList();
        }
    }

    public void Touch(string path, string name, int endpointCount)
    {
        lock (_lock)
        {
            var list = Load();
            list.RemoveAll(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, new RecentProject(name, path, DateTime.UtcNow, endpointCount));
            Save(list.Take(30).ToList());
        }
    }

    public void Remove(string path)
    {
        lock (_lock)
        {
            var list = Load();
            list.RemoveAll(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
            Save(list);
        }
    }

    private List<RecentProject> Load()
    {
        try
        {
            foreach (var candidate in new[] { _file, _file + ".bak" })
            {
                if (!File.Exists(candidate)) continue;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try { return JsonSerializer.Deserialize<List<RecentProject>>(File.ReadAllText(candidate), AppStorage.Json) ?? []; }
                    catch (IOException) { Thread.Sleep(50); } // another instance is writing: try again
                    catch (JsonException) { break; }          // unreadable: fall back to the backup
                }
            }
            return [];
        }
        catch { return []; }
    }

    private void Save(List<RecentProject> list)
    {
        Directory.CreateDirectory(AppStorage.Root);
        // Write to a temporary file and swap it in, keeping the previous version as a backup,
        // so a crash or a second instance can never leave a half-written (and then emptied) list.
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, AppStorage.Json));
        if (File.Exists(_file)) File.Replace(temp, _file, _file + ".bak", ignoreMetadataErrors: true);
        else File.Move(temp, _file);
    }
}
