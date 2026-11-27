using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QosriStudio.Server.Services.Contracts;

/// <summary>Writes an OpenAPI 3.0 document from a contract. Generic classes become one schema per use (PagedResultOfProductDto).</summary>
public static class OpenApiExporter
{
    public static string Export(ApiContract c, string? serverUrl)
    {
        var schemas = new JsonObject();
        var paths = new JsonObject();

        foreach (var op in c.Operations.OrderBy(o => o.Path).ThenBy(o => o.Method))
        {
            if (paths[op.Path] is not JsonObject item) paths[op.Path] = item = new JsonObject();
            var o = new JsonObject
            {
                ["tags"] = new JsonArray(op.Tag),
                ["operationId"] = op.Tag + "_" + op.Name,
            };
            if (!string.IsNullOrWhiteSpace(op.Summary)) o["summary"] = op.Summary;
            var parameters = new JsonArray();
            foreach (var p in op.PathParams) parameters.Add(Param(p, "path", true));
            foreach (var p in op.QueryParams) parameters.Add(Param(p, "query", !p.Optional));
            foreach (var p in op.HeaderParams) parameters.Add(Param(p, "header", !p.Optional));
            if (parameters.Count > 0) o["parameters"] = parameters;
            if (op.Body is not null)
                o["requestBody"] = new JsonObject
                {
                    ["required"] = !op.Body.Nullable,
                    ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Schema(op.Body) } },
                };
            var ok = op.Method == "POST" ? "201" : op.Response is null ? "204" : "200";
            var responses = new JsonObject();
            var success = new JsonObject { ["description"] = ok == "204" ? "Done" : "Success" };
            if (op.Response is not null) success["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Schema(op.Response) } };
            responses[ok] = success;
            if (op.Method == "POST" && op.Response is not null) responses["200"] = success.DeepClone();
            if (op.Body is not null) responses["400"] = new JsonObject { ["description"] = "The request isn't valid" };
            if (op.PathParams.Count > 0) responses["404"] = new JsonObject { ["description"] = "Not found" };
            if (op.RequiresAuth)
            {
                responses["401"] = new JsonObject { ["description"] = "Sign-in required" };
                o["security"] = new JsonArray(new JsonObject { ["Bearer"] = new JsonArray() });
            }
            o["responses"] = responses;
            item[op.Method.ToLowerInvariant()] = o;
        }

        var doc = new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject { ["title"] = c.Title, ["version"] = c.Version, ["description"] = "Exported by QosriStudio from the project's source code." },
        };
        if (!string.IsNullOrWhiteSpace(serverUrl)) doc["servers"] = new JsonArray(new JsonObject { ["url"] = serverUrl });
        doc["paths"] = paths;
        var components = new JsonObject { ["schemas"] = schemas };
        if (c.UsesAuth) components["securitySchemes"] = new JsonObject { ["Bearer"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer", ["bearerFormat"] = "JWT" } };
        doc["components"] = components;
        return doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        JsonObject Param(ParamDef p, string where, bool required) => new()
        {
            ["name"] = p.JsonName,
            ["in"] = where,
            ["required"] = required,
            ["schema"] = Schema(p.Type),
        };

        JsonNode Schema(TypeRef t)
        {
            JsonObject s;
            switch (t.Kind)
            {
                case "string" or "integer" or "number" or "boolean":
                    s = new JsonObject { ["type"] = t.Kind };
                    if (t.Format is not null) s["format"] = t.Format;
                    break;
                case "array": s = new JsonObject { ["type"] = "array", ["items"] = Schema(t.Element!) }; break;
                case "map": s = new JsonObject { ["type"] = "object", ["additionalProperties"] = Schema(t.Element!) }; break;
                case "ref":
                    var name = Closed(t);
                    s = new JsonObject { ["$ref"] = "#/components/schemas/" + name };
                    if (t.Nullable) return new JsonObject { ["allOf"] = new JsonArray(s), ["nullable"] = true };
                    return s;
                default: s = new JsonObject(); break;
            }
            if (t.Nullable) s["nullable"] = true;
            return s;
        }

        // Adds the schema for a (possibly generic) class and returns its name
        string Closed(TypeRef t)
        {
            if (!c.Types.TryGetValue(t.Name!, out var def)) return t.Name!;
            var args = t.Args ?? [];
            var name = args.Count == 0 ? def.Name : def.Name + "Of" + string.Join("And", args.Select(ArgName));
            if (schemas.ContainsKey(name)) return name;
            schemas[name] = new JsonObject(); // placeholder for recursion
            var obj = new JsonObject();
            if (def.Description is not null) obj["description"] = def.Description;
            if (def.Enum is not null)
            {
                if (def.StringEnum) { obj["type"] = "string"; obj["enum"] = new JsonArray(def.Enum.Select(e => (JsonNode)JsonValue.Create(e.Name)!).ToArray()); }
                else
                {
                    obj["type"] = "integer";
                    obj["enum"] = new JsonArray(def.Enum.Select(e => (JsonNode)JsonValue.Create(e.Value)!).ToArray());
                    obj["description"] = string.Join(", ", def.Enum.Select(e => $"{e.Value} = {e.Name}"));
                }
                schemas[name] = obj;
                return name;
            }
            var map = def.TypeParams.Zip(args).ToDictionary(x => x.First, x => x.Second);
            obj["type"] = "object";
            var props = new JsonObject();
            var required = new JsonArray();
            foreach (var p in def.Props)
            {
                var pt = Sub(p.Type, map);
                var ps = Schema(pt);
                if (p.Description is not null && ps is JsonObject po && !po.ContainsKey("$ref")) po["description"] = p.Description;
                props[p.JsonName] = ps;
                if (p.Required) required.Add(p.JsonName);
            }
            obj["properties"] = props;
            if (required.Count > 0) obj["required"] = required;
            schemas[name] = obj;
            return name;
        }

        string ArgName(TypeRef a) => a.Kind switch
        {
            "ref" => Closed(a),
            "array" => ArgName(a.Element!) + "List",
            "map" => ArgName(a.Element!) + "Map",
            "any" => "Object",
            _ => Regex.Replace(a.Format ?? a.Kind, "[^A-Za-z0-9]", "") is { Length: > 0 } f ? char.ToUpperInvariant(f[0]) + f[1..] : "Value",
        };
    }

    private static TypeRef Sub(TypeRef t, Dictionary<string, TypeRef> map) => t.Kind switch
    {
        "param" when t.Name is not null && map.TryGetValue(t.Name, out var v) => t.Nullable ? v.AsNullable() : v,
        "param" => TypeRef.Any,
        "array" or "map" => t with { Element = Sub(t.Element!, map) },
        "ref" when t.Args is not null => t with { Args = t.Args.Select(a => Sub(a, map)).ToList() },
        _ => t,
    };
}
