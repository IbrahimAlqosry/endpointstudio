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

    private void Commit(string projectPath, string description, List<(string Path, bool Existed)> files)
    {
        var id = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6];
        var entryDir = Path.Combine(Dir(projectPath), id);
        Directory.CreateDirectory(entryDir);
        var stored = new List<StoredFile>();
        var i = 0;
        foreach (var (path, existed) in files)
        {
            string? backup = null;
            if (existed)
            {
                // Byte-for-byte copy, so encodings and line endings come back exactly on undo
                backup = $"{i++}.bak";
                File.Copy(path, Path.Combine(entryDir, backup), overwrite: true);
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
        private readonly Dictionary<string, object> _writes = new(StringComparer.OrdinalIgnoreCase);

        public string Read(string path) =>
            _writes.TryGetValue(Path.GetFullPath(path), out var pending) ? pending as string ?? System.Text.Encoding.UTF8.GetString((byte[])pending) : File.ReadAllText(path);

        public bool Exists(string path) => _writes.ContainsKey(Path.GetFullPath(path)) || File.Exists(path);

        public void Write(string path, string content) => _writes[Path.GetFullPath(path)] = content;

        /// <summary>Writes exact bytes (used by the file editor to keep encodings).</summary>
        public void WriteBytes(string path, byte[] content) => _writes[Path.GetFullPath(path)] = content;

        public IReadOnlyCollection<string> ChangedFiles => _writes.Keys;

        public void Commit()
        {
            if (_writes.Count == 0) return;
            owner.Commit(projectPath, description, _writes.Keys.Select(p => (p, File.Exists(p))).ToList());
            foreach (var (path, content) in _writes)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (content is byte[] bytes) File.WriteAllBytes(path, bytes);
                else File.WriteAllText(path, (string)content);
            }
        }
    }
}
