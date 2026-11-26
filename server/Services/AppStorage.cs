using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EndpointStudio.Server.Models;

namespace EndpointStudio.Server.Services;

/// <summary>Location for the studio's own data (recent projects, change history).</summary>
public static class AppStorage
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EndpointStudio");

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
            return File.Exists(_file)
                ? JsonSerializer.Deserialize<List<RecentProject>>(File.ReadAllText(_file), AppStorage.Json) ?? []
                : [];
        }
        catch { return []; }
    }

    private void Save(List<RecentProject> list)
    {
        Directory.CreateDirectory(AppStorage.Root);
        File.WriteAllText(_file, JsonSerializer.Serialize(list, AppStorage.Json));
    }
}
