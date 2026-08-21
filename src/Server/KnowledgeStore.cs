using System.Text.Json.Nodes;

namespace Sts2McpBridge.Server;

public sealed class KnowledgeStore
{
    private static readonly string[] SupportedKinds =
    [
        "cards", "relics", "potions", "characters", "monsters", "powers", "events", "encounters",
        "enchantments", "keywords", "intents", "orbs", "afflictions", "modifiers", "achievements", "epochs"
    ];

    private readonly string _root;
    private readonly Dictionary<string, JsonArray> _collections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, JsonNode>> _indexes = new(StringComparer.OrdinalIgnoreCase);

    public KnowledgeStore(string root) => _root = root;

    public JsonNode Manifest()
    {
        string manifestPath = Path.Combine(Directory.GetParent(_root)?.FullName ?? _root, "manifest.json");
        JsonNode manifest = File.Exists(manifestPath) ? JsonNode.Parse(File.ReadAllText(manifestPath)) ?? new JsonObject() : new JsonObject();
        manifest["knowledge_root"] = _root;
        manifest["available"] = Directory.Exists(_root);
        manifest["supported_kinds"] = new JsonArray(SupportedKinds.Select(kind => (JsonNode?)kind).ToArray());
        return manifest;
    }

    public JsonNode Lookup(string kind, string id)
    {
        Dictionary<string, JsonNode> index = Index(NormalizeKind(kind));
        string normalized = NormalizeId(id);
        if (!index.TryGetValue(normalized, out JsonNode? entity)) throw new InvalidOperationException($"Knowledge entity not found: {kind}/{id}");
        return Envelope(kind, entity.DeepClone(), ExactCompleteness(kind, entity));
    }

    public JsonNode Search(string query, IReadOnlyList<string>? kinds, int limit)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new InvalidOperationException("query is required");
        string[] selected = kinds is { Count: > 0 } ? kinds.Select(NormalizeKind).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : SupportedKinds;
        var results = new JsonArray();
        foreach (string kind in selected)
        {
            foreach (JsonNode? entity in Collection(kind))
            {
                if (entity is not JsonObject value) continue;
                string text = value.ToJsonString();
                if (!text.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                results.Add(new JsonObject
                {
                    ["kind"] = kind,
                    ["id"] = value["id"]?.DeepClone(),
                    ["name"] = value["name"]?.DeepClone(),
                    ["entity"] = value.DeepClone(),
                    ["completeness"] = ExactCompleteness(kind, value)
                });
                if (results.Count >= Math.Clamp(limit, 1, 50)) return Result(results);
            }
        }
        return Result(results);
    }

    public JsonNode Relevant(IReadOnlyList<string> entityIds)
    {
        var results = new JsonArray();
        foreach (string raw in entityIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string id = NormalizeId(raw);
            foreach (string kind in SupportedKinds)
            {
                if (!Index(kind).TryGetValue(id, out JsonNode? entity)) continue;
                results.Add(new JsonObject
                {
                    ["kind"] = kind,
                    ["entity"] = entity.DeepClone(),
                    ["completeness"] = ExactCompleteness(kind, entity)
                });
                break;
            }
        }
        return Result(results);
    }

    private JsonNode Envelope(string kind, JsonNode entity, string completeness) => new JsonObject
    {
        ["source"] = "Spire Codex local noncommercial snapshot",
        ["kind"] = NormalizeKind(kind),
        ["completeness"] = completeness,
        ["dynamic_state_precedence"] = "Current game bridge state and NextMove override static knowledge.",
        ["entity"] = entity
    };

    private JsonNode Result(JsonArray results) => new JsonObject
    {
        ["source"] = "Spire Codex local noncommercial snapshot",
        ["dynamic_state_precedence"] = "Current game bridge state and NextMove override static knowledge.",
        ["results"] = results
    };

    private JsonArray Collection(string kind)
    {
        if (_collections.TryGetValue(kind, out JsonArray? cached)) return cached;
        string path = Path.Combine(_root, kind + ".json");
        if (!File.Exists(path)) throw new InvalidOperationException($"Knowledge file is unavailable: {path}");
        JsonArray collection = JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? throw new InvalidOperationException($"Knowledge file is not a JSON array: {path}");
        _collections[kind] = collection;
        return collection;
    }

    private Dictionary<string, JsonNode> Index(string kind)
    {
        if (_indexes.TryGetValue(kind, out Dictionary<string, JsonNode>? cached)) return cached;
        var index = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? node in Collection(kind))
        {
            string? id = node?["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id)) index[NormalizeId(id)] = node!;
        }
        _indexes[kind] = index;
        return index;
    }

    private static string NormalizeKind(string kind)
    {
        string normalized = kind.Trim().ToLowerInvariant();
        if (!SupportedKinds.Contains(normalized, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"Unsupported knowledge kind: {kind}");
        return normalized;
    }

    private static string NormalizeId(string id)
    {
        string normalized = id.Trim().ToUpperInvariant();
        int separator = normalized.IndexOf('.');
        return separator >= 0 ? normalized[(separator + 1)..] : normalized;
    }

    private static string ExactCompleteness(string kind, JsonNode entity)
    {
        if (kind.Equals("monsters", StringComparison.OrdinalIgnoreCase))
        {
            JsonNode? pattern = entity["attack_pattern"];
            if (pattern is null) return "partial: no parsed attack pattern";
            bool emptyBranches = pattern["states"] is JsonArray states && states.Any(state => state?["type"]?.GetValue<string>() is "random" or "conditional" && state?["branches"] is JsonArray branches && branches.Count == 0);
            return emptyBranches ? "partial: attack pattern has unresolved branches" : "parsed static attack pattern";
        }
        return "parsed static game data";
    }
}
