using System.Text.Json;
using System.Text.Json.Nodes;
using Sts2McpBridge.Core;
using Sts2McpBridge.Server;

List<(string Name, Func<Task> Run)> tests =
[
    ("token authentication", TokenAuthentication),
    ("state version rejection", StateVersionRejection),
    ("action id validation", ActionIdValidation),
    ("MCP tools list", McpToolsList),
    ("MCP tools call dispatch", McpToolDispatch),
    ("knowledge lookup and completeness", KnowledgeLookup),
    ("JSON-RPC errors and ids", JsonRpcErrors),
    ("secret never appears in output", NoSecretOutput)
];

int failures = 0;
foreach ((string name, Func<Task> run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {name}: {exception.Message}"); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} tests passed");
return failures == 0 ? 0 : 1;

static Task TokenAuthentication()
{
    BridgeStore store = new("correct-secret-token");
    Assert(store.Authenticate("correct-secret-token"), "correct token rejected");
    Assert(!store.Authenticate("wrong-secret-token"), "wrong token accepted");
    Assert(!store.Authenticate(null), "missing token accepted");
    return Task.CompletedTask;
}

static Task StateVersionRejection()
{
    BridgeStore store = StoreWithState();
    ActionResponse result = store.Queue(new("correct-secret-token", 6, "combat:play"));
    Assert(!result.Accepted && result.Message.Contains("Stale", StringComparison.Ordinal), "stale state accepted");
    return Task.CompletedTask;
}

static Task ActionIdValidation()
{
    BridgeStore store = StoreWithState();
    Assert(!store.Queue(new("correct-secret-token", 7, "combat:unknown")).Accepted, "unknown action accepted");
    Assert(store.Queue(new("correct-secret-token", 7, "combat:play")).Accepted, "legal action rejected");
    PendingAction? pending = store.TakePending(7);
    Assert(pending?.ActionId == "combat:play", "legal action was not queued");
    return Task.CompletedTask;
}

static async Task McpToolsList()
{
    McpServer server = new(new FakeBridgeApi());
    string response = Required(await server.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}"));
    JsonNode root = JsonNode.Parse(response)!;
    JsonArray tools = root["result"]!["tools"]!.AsArray();
    Assert(tools.Count == 11, $"expected 11 tools, got {tools.Count}");
    Assert(tools.Any(tool => tool?["name"]?.GetValue<string>() == "sts2_execute_action"), "execute tool missing");
    Assert(tools.Any(tool => tool?["name"]?.GetValue<string>() == "sts2_knowledge_lookup"), "knowledge lookup tool missing");
}

static async Task McpToolDispatch()
{
    FakeBridgeApi bridge = new();
    McpServer server = new(bridge);
    string request = "{\"jsonrpc\":\"2.0\",\"id\":\"call-1\",\"method\":\"tools/call\",\"params\":{\"name\":\"sts2_execute_action\",\"arguments\":{\"state_version\":7,\"action_id\":\"combat:play\"}}}";
    string response = Required(await server.HandleLineAsync(request));
    Assert(bridge.Executed == (7, "combat:play"), "execute arguments were not dispatched");
    JsonNode root = JsonNode.Parse(response)!;
    Assert(root["id"]?.GetValue<string>() == "call-1", "string id was not preserved");
    Assert(root["result"]?["content"]?[0]?["type"]?.GetValue<string>() == "text", "tool result is not MCP text content");
}

static async Task JsonRpcErrors()
{
    McpServer server = new(new FakeBridgeApi());
    JsonNode parse = JsonNode.Parse(Required(await server.HandleLineAsync("{")))!;
    Assert(parse["error"]?["code"]?.GetValue<int>() == -32700, "parse error code mismatch");
    JsonNode method = JsonNode.Parse(Required(await server.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"missing\"}")))!;
    Assert(method["error"]?["code"]?.GetValue<int>() == -32601, "method error code mismatch");
    Assert(method["id"]?.GetValue<int>() == 9, "numeric id was not preserved");
    JsonNode invalid = JsonNode.Parse(Required(await server.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"id\":{},\"method\":4}")))!;
    Assert(invalid["error"]?["code"]?.GetValue<int>() == -32600, "invalid request code mismatch");
    string? notification = await server.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
    Assert(notification is null, "notification produced a response");
}

static async Task KnowledgeLookup()
{
    string root = Path.Combine(Path.GetTempPath(), "sts2-mcp-knowledge-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        await File.WriteAllTextAsync(Path.Combine(root, "monsters.json"), "[{\"id\":\"SLUDGE_SPINNER\",\"name\":\"Sludge Spinner\",\"attack_pattern\":{\"states\":[{\"type\":\"random\",\"branches\":[]}]}}]");
        foreach (string kind in new[] { "cards", "relics", "potions", "characters", "powers", "events", "encounters", "enchantments", "keywords", "intents", "orbs", "afflictions", "modifiers", "achievements", "epochs" })
            await File.WriteAllTextAsync(Path.Combine(root, kind + ".json"), "[]");
        KnowledgeStore knowledge = new(root);
        JsonNode result = knowledge.Lookup("monsters", "MONSTER.SLUDGE_SPINNER");
        Assert(result["entity"]?["name"]?.GetValue<string>() == "Sludge Spinner", "knowledge lookup failed");
        Assert(result["completeness"]?.GetValue<string>()?.Contains("unresolved", StringComparison.Ordinal) == true, "partial state machine was not marked");
        McpServer server = new(new FakeBridgeApi(), knowledge);
        string request = "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"sts2_knowledge_lookup\",\"arguments\":{\"kind\":\"monsters\",\"id\":\"SLUDGE_SPINNER\"}}}";
        string response = Required(await server.HandleLineAsync(request));
        Assert(response.Contains("Sludge Spinner", StringComparison.Ordinal), "knowledge MCP output missing entity");
    }
    finally { Directory.Delete(root, true); }
}

static async Task NoSecretOutput()
{
    const string secret = "super-secret-token-never-print";
    McpServer server = new(new FakeBridgeApi());
    string[] requests =
    [
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}",
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
        "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"sts2_get_state\",\"arguments\":{}}}"
    ];
    foreach (string request in requests) Assert(!Required(await server.HandleLineAsync(request)).Contains(secret, StringComparison.Ordinal), "secret appeared in MCP output");
}

static BridgeStore StoreWithState()
{
    BridgeStore store = new("correct-secret-token");
    JsonElement observation = JsonSerializer.SerializeToElement(new { hp = 50 }, BridgeJson.Options);
    store.Register(new(7, "run", "combat", false, observation, [new("combat:play", "play_card", "Play Strike")], DateTimeOffset.UtcNow));
    return store;
}

static string Required(string? value) => value ?? throw new Exception("expected a response");
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

sealed class FakeBridgeApi : IBridgeApi
{
    public (long Version, string ActionId)? Executed { get; private set; }
    public Task<JsonNode?> GetStateAsync(CancellationToken cancellationToken) => Task.FromResult<JsonNode?>(JsonNode.Parse("{\"state_version\":7,\"paused\":false,\"legal_actions\":[{\"action_id\":\"combat:play\"}]}"));
    public Task<JsonNode?> ExecuteAsync(long stateVersion, string actionId, CancellationToken cancellationToken) { Executed = (stateVersion, actionId); return Task.FromResult<JsonNode?>(new JsonObject { ["accepted"] = true }); }
    public Task<JsonNode?> PauseAsync(bool paused, CancellationToken cancellationToken) => Task.FromResult<JsonNode?>(new JsonObject { ["paused"] = paused });
    public Task<JsonNode?> GetHistoryAsync(int limit, CancellationToken cancellationToken) => Task.FromResult<JsonNode?>(new JsonObject { ["history"] = new JsonArray() });
}
