using Stackwright.Server.Models;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Stackwright.Server.Services;

public record SettingEntry(string Key, string? Value, string Kind, bool Secret);
public record SettingsFile(string Name, string Path, string Format, string? Environment, bool HasComments, List<SettingEntry> Entries);
public record SettingsInfo(List<SettingsFile> Files, List<string> Environments, List<LaunchProfile> LaunchProfiles);
public record LaunchProfile(string Name, string? Urls, string? Environment);
public record SettingChange(string Key, string? Value, string Kind, bool Remove);
public record SaveSettingsRequest(string File, List<SettingChange> Changes);

/// <summary>Reads and edits appsettings*.json and Web.config as simple key/value lists.</summary>
public class SettingsService(HistoryService history)
{
    private static readonly Regex SecretRx = new("password|pwd|secret|token|apikey|api_key|privatekey|connectionstring|clientsecret|signingkey|key$", RegexOptions.IgnoreCase);
    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public SettingsInfo Load(string csproj)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(csproj))!;
        var files = new List<SettingsFile>();
        foreach (var path in Directory.EnumerateFiles(dir, "appsettings*.json").OrderBy(f => f.Length).ThenBy(f => f))
        {
            var text = File.ReadAllText(path);
            var entries = new List<SettingEntry>();
            try
            {
                var node = JsonNode.Parse(text, documentOptions: ReadOptions);
                Flatten(node, "", entries);
            }
            catch (JsonException ex) { entries.Add(new SettingEntry("(file can't be read)", ex.Message, "error", false)); }
            var env = Regex.Match(Path.GetFileName(path), @"^appsettings\.(.+)\.json$", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
            files.Add(new SettingsFile(Path.GetFileName(path), path, "json", env, HasComments(text), entries));
        }
        var webConfig = Path.Combine(dir, "Web.config");
        if (File.Exists(webConfig)) files.Add(LoadWebConfig(webConfig));

        var profiles = new List<LaunchProfile>();
        var launch = Path.Combine(dir, "Properties", "launchSettings.json");
        if (File.Exists(launch))
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(launch), documentOptions: ReadOptions);
                if (node?["profiles"] is JsonObject ps)
                    foreach (var (name, p) in ps)
                        profiles.Add(new LaunchProfile(name, p?["applicationUrl"]?.GetValue<string>(), p?["environmentVariables"]?["ASPNETCORE_ENVIRONMENT"]?.GetValue<string>()));
            }
            catch { /* ignore */ }
        }
        var envs = files.Where(f => f.Environment is not null).Select(f => f.Environment!).ToList();
        return new SettingsInfo(files, envs, profiles);
    }

    private static void Flatten(JsonNode? node, string prefix, List<SettingEntry> into)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.Count == 0 && prefix.Length > 0) into.Add(new SettingEntry(prefix, null, "section", false));
                foreach (var (k, v) in obj) Flatten(v, prefix.Length == 0 ? k : $"{prefix}:{k}", into);
                break;
            case JsonArray arr:
                if (arr.Count == 0) into.Add(new SettingEntry(prefix, "[]", "array", false));
                for (var i = 0; i < arr.Count; i++) Flatten(arr[i], $"{prefix}:{i}", into);
                break;
            case JsonValue val:
                var kind = val.GetValueKind() switch
                {
                    JsonValueKind.Number => "number",
                    JsonValueKind.True or JsonValueKind.False => "bool",
                    _ => "string",
                };
                into.Add(new SettingEntry(prefix, kind == "string" ? val.GetValue<string>() : val.ToJsonString(), kind, SecretRx.IsMatch(prefix.Split(':').Last()) || SecretRx.IsMatch(prefix) && kind == "string" && LooksSecret(val.ToJsonString())));
                break;
            default:
                into.Add(new SettingEntry(prefix, null, "null", false));
                break;
        }
    }

    private static bool LooksSecret(string v) => Regex.IsMatch(v, "password=|pwd=", RegexOptions.IgnoreCase);

    private static bool HasComments(string json) =>
        Regex.IsMatch(Regex.Replace(json, @"""(\\.|[^""\\])*""", "\"\""), @"//|/\*");

    private static SettingsFile LoadWebConfig(string path)
    {
        var entries = new List<SettingEntry>();
        try
        {
            var doc = XDocument.Load(path);
            foreach (var add in doc.Descendants("connectionStrings").Descendants("add"))
                entries.Add(new SettingEntry($"connectionStrings:{(string?)add.Attribute("name")}", (string?)add.Attribute("connectionString"), "string", true));
            foreach (var add in doc.Descendants("appSettings").Descendants("add"))
            {
                var key = (string?)add.Attribute("key") ?? "";
                entries.Add(new SettingEntry($"appSettings:{key}", (string?)add.Attribute("value"), "string", SecretRx.IsMatch(key)));
            }
        }
        catch (Exception ex) { entries.Add(new SettingEntry("(file can't be read)", ex.Message, "error", false)); }
        return new SettingsFile("Web.config", path, "xml", null, false, entries);
    }

    public string Save(string csproj, SaveSettingsRequest req)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(csproj))!;
        var path = Path.GetFullPath(Path.Combine(dir, Path.GetFileName(req.File)));
        if (!File.Exists(path)) throw new StudioException("That settings file doesn't exist anymore.");
        var changes = history.Begin(csproj, $"Changed settings in {Path.GetFileName(path)}");
        var text = File.ReadAllText(path);
        string updated;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var root = JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject ?? new JsonObject();
            foreach (var c in req.Changes) Apply(root, c);
            updated = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            if (text.Contains("\r\n")) updated = updated.Replace("\n", "\r\n");
            updated += text.Contains("\r\n") ? "\r\n" : "\n";
        }
        else
        {
            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            foreach (var c in req.Changes) ApplyWebConfig(doc, c);
            updated = doc.Declaration is null ? doc.ToString(SaveOptions.DisableFormatting) : doc.Declaration + Environment.NewLine + doc.ToString(SaveOptions.DisableFormatting);
        }
        changes.Write(path, updated);
        changes.Commit();
        return $"Saved {req.Changes.Count} change{(req.Changes.Count == 1 ? "" : "s")} to {Path.GetFileName(path)}.";
    }

    public string AddEnvironment(string csproj, string name)
    {
        if (!Regex.IsMatch(name ?? "", @"^[A-Za-z][A-Za-z0-9]*$")) throw new StudioException("Use letters and numbers for the environment name, for example Staging.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(csproj))!;
        var path = Path.Combine(dir, $"appsettings.{name}.json");
        if (File.Exists(path)) throw new StudioException($"The {name} environment already exists.");
        var changes = history.Begin(csproj, $"Added the {name} environment");
        changes.Write(path, "{\r\n}\r\n");
        changes.Commit();
        return $"Added appsettings.{name}.json. Settings in it override appsettings.json when the app runs as {name}.";
    }

    private static void Apply(JsonObject root, SettingChange c)
    {
        var parts = c.Key.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new StudioException("A setting needs a name.");
        JsonNode current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i];
            if (current is JsonArray arr && int.TryParse(part, out var idx)) { current = arr[idx] ?? throw new StudioException($"“{c.Key}” doesn't exist."); continue; }
            if (current is not JsonObject obj) throw new StudioException($"“{c.Key}” can't be changed here.");
            if (obj[part] is null)
            {
                if (c.Remove) return;
                obj[part] = new JsonObject();
            }
            current = obj[part]!;
        }
        var last = parts[^1];
        if (current is JsonArray array && int.TryParse(last, out var i2))
        {
            if (c.Remove) array.RemoveAt(i2); else array[i2] = Value(c);
            return;
        }
        if (current is not JsonObject target) throw new StudioException($"“{c.Key}” can't be changed here.");
        if (c.Remove) target.Remove(last); else target[last] = Value(c);
    }

    private static JsonNode? Value(SettingChange c) => c.Kind switch
    {
        "number" => decimal.TryParse(c.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : throw new StudioException($"“{c.Key}” must be a number."),
        "bool" => JsonValue.Create(string.Equals(c.Value, "true", StringComparison.OrdinalIgnoreCase)),
        "null" => null,
        "section" => new JsonObject(),
        _ => JsonValue.Create(c.Value ?? ""),
    };

    private static void ApplyWebConfig(XDocument doc, SettingChange c)
    {
        var parts = c.Key.Split(':', 2);
        if (parts.Length != 2) throw new StudioException("Web.config settings look like connectionStrings:Name or appSettings:Key.");
        var (section, name) = (parts[0], parts[1]);
        var root = doc.Root ?? throw new StudioException("Web.config can't be read.");
        if (section.Equals("connectionStrings", StringComparison.OrdinalIgnoreCase))
        {
            var cs = root.Element("connectionStrings") ?? new XElement("connectionStrings");
            if (cs.Parent is null) root.AddFirst(cs);
            var add = cs.Elements("add").FirstOrDefault(a => (string?)a.Attribute("name") == name);
            if (c.Remove) { add?.Remove(); return; }
            if (add is null) cs.Add(add = new XElement("add", new XAttribute("name", name), new XAttribute("providerName", "System.Data.SqlClient")));
            add.SetAttributeValue("connectionString", c.Value ?? "");
        }
        else if (section.Equals("appSettings", StringComparison.OrdinalIgnoreCase))
        {
            var app = root.Element("appSettings") ?? new XElement("appSettings");
            if (app.Parent is null) root.AddFirst(app);
            var add = app.Elements("add").FirstOrDefault(a => (string?)a.Attribute("key") == name);
            if (c.Remove) { add?.Remove(); return; }
            if (add is null) app.Add(add = new XElement("add", new XAttribute("key", name)));
            add.SetAttributeValue("value", c.Value ?? "");
        }
        else throw new StudioException("Only connectionStrings and appSettings can be edited in Web.config.");
    }
}
