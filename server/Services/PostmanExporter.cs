using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QosriStudio.Server.Models;

namespace QosriStudio.Server.Services;

/// <summary>Turns a project's endpoints into a Postman collection (v2.1).</summary>
public static class PostmanExporter
{
    public static string Export(ProjectModel model, string? baseUrl)
    {
        var collection = new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["name"] = model.Name,
                ["description"] = $"Exported from QosriStudio ({model.Endpoints.Count} endpoints).",
                ["schema"] = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json",
            },
            ["variable"] = new JsonArray(new JsonObject { ["key"] = "baseUrl", ["value"] = baseUrl ?? "http://localhost:5000" }),
        };

        var folders = new JsonArray();
        foreach (var group in model.Groups.Where(g => g.Endpoints.Count > 0).OrderBy(g => g.Name))
        {
            var items = new JsonArray();
            foreach (var ep in group.Endpoints.Select(e => e.Info))
                items.Add(Request(ep, model));
            folders.Add(new JsonObject { ["name"] = group.Name, ["item"] = items });
        }
        collection["item"] = folders;
        return collection.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Request(EndpointInfo ep, ProjectModel model)
    {
        var path = Regex.Replace(ep.Route, @"\{\*{0,2}([A-Za-z_][A-Za-z0-9_]*)[^}]*\}", ":$1");
        var segments = new JsonArray(path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        var query = new JsonArray(ep.Parameters.Where(p => p.Source == ParamSource.Query)
            .Select(p => (JsonNode)new JsonObject { ["key"] = p.Name, ["value"] = "", ["disabled"] = p.Optional }).ToArray());
        var vars = new JsonArray(ep.Parameters.Where(p => p.Source == ParamSource.Route)
            .Select(p => (JsonNode)new JsonObject { ["key"] = p.Name, ["value"] = "" }).ToArray());
        var headers = new JsonArray(ep.Parameters.Where(p => p.Source == ParamSource.Header)
            .Select(p => (JsonNode)new JsonObject { ["key"] = p.Name, ["value"] = "" }).ToArray());

        var raw = "{{baseUrl}}" + path;
        var q = ep.Parameters.Where(p => p.Source == ParamSource.Query && !p.Optional).Select(p => $"{p.Name}=").ToList();
        if (q.Count > 0) raw += "?" + string.Join("&", q);

        var request = new JsonObject
        {
            ["method"] = ep.HttpMethod == "ANY" ? "GET" : ep.HttpMethod,
            ["header"] = headers,
            ["url"] = new JsonObject
            {
                ["raw"] = raw,
                ["host"] = new JsonArray("{{baseUrl}}"),
                ["path"] = segments,
                ["query"] = query,
                ["variable"] = vars,
            },
        };
        if (!string.IsNullOrWhiteSpace(ep.Summary)) request["description"] = ep.Summary;

        var body = ep.Parameters.FirstOrDefault(p => p.Source == ParamSource.Body);
        if (body is not null)
        {
            request["body"] = new JsonObject
            {
                ["mode"] = "raw",
                ["raw"] = SampleBody(body.Type, model),
                ["options"] = new JsonObject { ["raw"] = new JsonObject { ["language"] = "json" } },
            };
        }

        return new JsonObject
        {
            ["name"] = string.IsNullOrEmpty(ep.Name) ? $"{ep.HttpMethod} {ep.Route}" : ProjectAnalyzer.SplitWords(ep.Name),
            ["request"] = request,
        };
    }

    public static string SampleBody(string type, ProjectModel model)
    {
        var entity = model.Entities.FirstOrDefault(e => e.Name == type);
        if (entity is null) return "{\n  \n}";
        var obj = new JsonObject();
        foreach (var p in entity.Properties) obj[p.Name] = SampleValue(p.Type);
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonNode? SampleValue(string type) => type switch
    {
        "string" => "",
        "int" or "long" or "short" or "byte" or "uint" or "ulong" or "ushort" or "sbyte" => 0,
        "decimal" or "double" or "float" => 0.0,
        "bool" => false,
        "Guid" => Guid.Empty.ToString(),
        "DateTime" or "DateTimeOffset" => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"),
        "DateOnly" => DateTime.UtcNow.ToString("yyyy-MM-dd"),
        "TimeOnly" or "TimeSpan" => "00:00:00",
        _ => null,
    };
}
