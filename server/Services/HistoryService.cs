using System.Text.Json;
using QosriStudio.Server.Models;

namespace QosriStudio.Server.Services;

/// <summary>
/// Keeps a copy of every file before the studio changes it, so any change can be undone.
/// Stored outside the user's project so it never pollutes their source tree.
/// </summary>
public class HistoryService
{
    private const int MaxEntries = 50;

    private record StoredFile(string Path, bool Existed, string? BackupName);
    private record StoredEntry(string Id, DateTime Time, string Description, List<StoredFile> Files);

    private static string Dir(string projectPath) => Path.Combine(AppStorage.Root, "history", AppStorage.ProjectKey(projectPath));
    private static string IndexFile(string projectPath) => Path.Combine(Dir(projectPath), "history.json");

    public List<HistoryEntry> GetEntries(string projectPath) =>
        Load(projectPath).Select(e => new HistoryEntry(e.Id, e.Time, e.Description)).ToList();

    public void Clear(string projectPath) => TryDelete(Dir(projectPath));

    public ChangeSet Begin(string projectPath, string description) => new(this, projectPath, description);

    public string UndoLast(string projectPath)
    {
        var entries = Load(projectPath);
        if (entries.Count == 0) throw new StudioException("There is nothing to undo.");
        var last = entries[0];
        var entryDir = Path.Combine(Dir(projectPath), last.Id);
        foreach (var f in last.Files)
        {
            if (f.Existed && f.BackupName is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(f.Path)!);
                File.Copy(Path.Combine(entryDir, f.BackupName), f.Path, overwrite: true);
            }
            else if (!f.Existed && File.Exists(f.Path))
            {
                File.Delete(f.Path);
            }
        }
        entries.RemoveAt(0);
        Save(projectPath, entries);
        TryDelete(entryDir);
        return last.Description;
    }

    private void Commit(string projectPath, string description, List<(string Path, bool Existed, string? Content)> files)
    {
        var id = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6];
        var entryDir = Path.Combine(Dir(projectPath), id);
        Directory.CreateDirectory(entryDir);
        var stored = new List<StoredFile>();
        var i = 0;
        foreach (var (path, existed, content) in files)
        {
            string? backup = null;
            if (existed && content is not null)
            {
                backup = $"{i++}.bak";
                File.WriteAllText(Path.Combine(entryDir, backup), content);
            }
            stored.Add(new StoredFile(path, existed, backup));
        }

        var entries = Load(projectPath);
        entries.Insert(0, new StoredEntry(id, DateTime.UtcNow, description, stored));
        foreach (var old in entries.Skip(MaxEntries)) TryDelete(Path.Combine(Dir(projectPath), old.Id));
        Save(projectPath, entries.Take(MaxEntries).ToList());
    }

    private static List<StoredEntry> Load(string projectPath)
    {
        try
        {
            var f = IndexFile(projectPath);
            return File.Exists(f) ? JsonSerializer.Deserialize<List<StoredEntry>>(File.ReadAllText(f), AppStorage.Json) ?? [] : [];
        }
        catch { return []; }
    }

    private static void Save(string projectPath, List<StoredEntry> entries)
    {
        Directory.CreateDirectory(Dir(projectPath));
        File.WriteAllText(IndexFile(projectPath), JsonSerializer.Serialize(entries, AppStorage.Json));
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* ignore */ }
    }

    /// <summary>Collects file writes; nothing is written until <see cref="Commit"/>.</summary>
    public class ChangeSet(HistoryService owner, string projectPath, string description)
    {
        private readonly Dictionary<string, string> _writes = new(StringComparer.OrdinalIgnoreCase);

        public string Read(string path) =>
            _writes.TryGetValue(path, out var pending) ? pending : File.ReadAllText(path);

        public bool Exists(string path) => _writes.ContainsKey(path) || File.Exists(path);

        public void Write(string path, string content) => _writes[Path.GetFullPath(path)] = content;

        public IReadOnlyCollection<string> ChangedFiles => _writes.Keys;

        public void Commit()
        {
            if (_writes.Count == 0) return;
            var originals = _writes.Keys
                .Select(p => (p, File.Exists(p), File.Exists(p) ? File.ReadAllText(p) : null))
                .ToList();
            owner.Commit(projectPath, description, originals);
            foreach (var (path, content) in _writes)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }
        }
    }
}
