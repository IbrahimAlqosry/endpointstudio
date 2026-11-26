using EndpointStudio.Server.Models;

namespace EndpointStudio.Server.Services;

public class FileSystemService
{
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", "packages", "$RECYCLE.BIN", "System Volume Information"
    };

    public List<DriveEntry> GetDrives() =>
        DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new DriveEntry(d.Name.TrimEnd('\\'), d.RootDirectory.FullName, SafeLabel(d)))
            .ToList();

    private static string? SafeLabel(DriveInfo d)
    {
        try { return string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel; } catch { return null; }
    }

    public BrowseResult Browse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var drives = GetDrives().Select(d => new FolderEntry(d.Label is null ? d.Name : $"{d.Name} ({d.Label})", d.Path)).ToList();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            drives.Insert(0, new FolderEntry("Home", home));
            return new BrowseResult(null, null, drives, []);
        }

        var dir = new DirectoryInfo(path);
        if (!dir.Exists) throw new StudioException("That folder doesn't exist anymore.");

        var folders = new List<FolderEntry>();
        try
        {
            foreach (var d in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (SkipFolders.Contains(d.Name) || d.Name.StartsWith('.')) continue;
                if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                folders.Add(new FolderEntry(d.Name, d.FullName));
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new StudioException("You don't have permission to open this folder.");
        }

        var projects = dir.EnumerateFiles("*.csproj")
            .Select(f => new ProjectFileEntry(Path.GetFileNameWithoutExtension(f.Name), f.FullName))
            .ToList();

        return new BrowseResult(dir.FullName, dir.Parent?.FullName, folders, projects);
    }

    /// <summary>Finds .csproj files below a folder (limited depth).</summary>
    public List<ProjectFileEntry> FindProjects(string root, int maxDepth = 4)
    {
        var result = new List<ProjectFileEntry>();
        if (!Directory.Exists(root)) throw new StudioException("That folder doesn't exist.");
        Walk(new DirectoryInfo(root), 0);
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

        void Walk(DirectoryInfo d, int depth)
        {
            if (result.Count >= 200) return;
            try
            {
                foreach (var f in d.EnumerateFiles("*.csproj"))
                    result.Add(new ProjectFileEntry(Path.GetFileNameWithoutExtension(f.Name), f.FullName));
                if (depth >= maxDepth) return;
                foreach (var sub in d.EnumerateDirectories())
                {
                    if (SkipFolders.Contains(sub.Name) || sub.Name.StartsWith('.')) continue;
                    Walk(sub, depth + 1);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
    }
}
