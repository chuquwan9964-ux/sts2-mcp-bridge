using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sts2McpBridge.Core;

namespace Sts2McpBridge.Server;

public interface IBridgeApi
{
    Task<JsonNode?> GetStateAsync(CancellationToken cancellationToken);
    Task<JsonNode?> ExecuteAsync(long stateVersion, string actionId, CancellationToken cancellationToken);
    Task<JsonNode?> PauseAsync(bool paused, CancellationToken cancellationToken);
    Task<JsonNode?> GetHistoryAsync(int limit, CancellationToken cancellationToken);
}

public sealed class HttpBridgeApi : IBridgeApi, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _token;

    public HttpBridgeApi(Uri baseUri, string token)
    {
        _token = token;
        _client = new() { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(10) };
        _client.DefaultRequestHeaders.Authorization = new("Bearer", token);
    }

    public Task<JsonNode?> GetStateAsync(CancellationToken cancellationToken) => SendAsync(HttpMethod.Get, "v1/state", null, cancellationToken);
    public Task<JsonNode?> PauseAsync(bool paused, CancellationToken cancellationToken) => SendAsync(HttpMethod.Post, paused ? "v1/pause" : "v1/resume", new { }, cancellationToken);
    public Task<JsonNode?> GetHistoryAsync(int limit, CancellationToken cancellationToken) => SendAsync(HttpMethod.Get, $"v1/history?limit={Math.Clamp(limit, 1, 200)}", null, cancellationToken);
    public Task<JsonNode?> ExecuteAsync(long stateVersion, string actionId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "v1/action", new ActionRequest(_token, stateVersion, actionId), cancellationToken);

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: BridgeJson.Options);
        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
        JsonNode? result = await response.Content.ReadFromJsonAsync<JsonNode>(BridgeJson.Options, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string message = result?["error"]?.GetValue<string>() ?? result?["message"]?.GetValue<string>() ?? $"HTTP {(int)response.StatusCode}";
            throw new InvalidOperationException(message);
        }
        return result;
    }

    public void Dispose() => _client.Dispose();
}

public sealed class McpServer(IBridgeApi bridge)
{
    private static readonly JsonArray Tools = BuildTools();

    public async Task<string?> HandleLineAsync(string line, CancellationToken cancellationToken = default)
    {
        JsonNode? request;
        try { request = JsonNode.Parse(line); }
        catch (JsonException) { return Error(null, -32700, "Parse error").ToJsonString(); }
        if (request is not JsonObject root) return Error(null, -32600, "Invalid Request").ToJsonString();
        JsonNode? id = root["id"]?.DeepClone();
        if (id is JsonObject or JsonArray) return Error(null, -32600, "Invalid Request").ToJsonString();
        string? method = StringValue(root["method"]);
        if (method is null || StringValue(root["jsonrpc"]) != "2.0") return Error(id, -32600, "Invalid Request").ToJsonString();
        if (method == "notifications/initialized") return null;
        try
        {
            JsonNode result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = "2025-03-26",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "sts2-mcp-bridge", ["version"] = "0.1.0" }
                },
                "tools/list" => new JsonObject { ["tools"] = Tools.DeepClone() },
                "tools/call" => await CallToolAsync(root["params"] as JsonObject, cancellationToken),
                _ => throw new RpcException(-32601, "Method not found")
            };
            return Success(id, result).ToJsonString();
        }
        catch (RpcException exception) { return Error(id, exception.Code, exception.Message).ToJsonString(); }
        catch (Exception exception) { return Success(id, ToolError(exception.Message)).ToJsonString(); }
    }

    private async Task<JsonNode> CallToolAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        string name = parameters?["name"]?.GetValue<string>() ?? throw new RpcException(-32602, "Tool name is required");
        JsonObject arguments = parameters?["arguments"] as JsonObject ?? new();
        JsonNode? value = name switch
        {
            "sts2_get_state" => await bridge.GetStateAsync(cancellationToken),
            "sts2_get_legal_actions" => LegalActions(await bridge.GetStateAsync(cancellationToken)),
            "sts2_execute_action" => await bridge.ExecuteAsync(RequiredLong(arguments, "state_version"), RequiredString(arguments, "action_id"), cancellationToken),
            "sts2_wait_for_state_change" => await WaitForChangeAsync(RequiredLong(arguments, "state_version"), OptionalInt(arguments, "timeout_ms", 10_000), cancellationToken),
            "sts2_pause" => await bridge.PauseAsync(true, cancellationToken),
            "sts2_resume" => await bridge.PauseAsync(false, cancellationToken),
            "sts2_get_history" => await bridge.GetHistoryAsync(OptionalInt(arguments, "limit", 50), cancellationToken),
            _ => throw new RpcException(-32602, $"Unknown tool: {name}")
        };
        return ToolResult(value);
    }

    private async Task<JsonNode?> WaitForChangeAsync(long version, int timeoutMs, CancellationToken cancellationToken)
    {
        timeoutMs = Math.Clamp(timeoutMs, 100, 30_000);
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        do
        {
            JsonNode? state = await bridge.GetStateAsync(cancellationToken);
            if (state?["state_version"]?.GetValue<long>() != version) return state;
            await Task.Delay(200, cancellationToken);
        } while (DateTime.UtcNow < deadline);
        return new JsonObject { ["changed"] = false, ["state_version"] = version };
    }

    private static JsonNode? LegalActions(JsonNode? state) => new JsonObject
    {
        ["state_version"] = state?["state_version"]?.DeepClone(),
        ["paused"] = state?["paused"]?.DeepClone(),
        ["legal_actions"] = state?["legal_actions"]?.DeepClone() ?? new JsonArray()
    };

    private static JsonObject ToolResult(JsonNode? value) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = (value ?? new JsonObject()).ToJsonString() })
    };

    private static JsonObject ToolError(string message) => new()
    {
        ["isError"] = true,
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message })
    };

    private static long RequiredLong(JsonObject arguments, string name) => arguments[name]?.GetValue<long>() ?? throw new RpcException(-32602, $"{name} is required");
    private static string RequiredString(JsonObject arguments, string name) => arguments[name]?.GetValue<string>() ?? throw new RpcException(-32602, $"{name} is required");
    private static int OptionalInt(JsonObject arguments, string name, int fallback) => arguments[name]?.GetValue<int>() ?? fallback;
    private static JsonObject Success(JsonNode? id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    private static JsonObject Error(JsonNode? id, int code, string message) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
    private static string? StringValue(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static JsonArray BuildTools()
    {
        static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["additionalProperties"] = false
            }
        };
        JsonObject version = new() { ["state_version"] = new JsonObject { ["type"] = "integer", ["description"] = "Exact observed state version." } };
        return new(
            Tool("sts2_get_state", "Get the latest structured STS2 observation and legal actions.", new()),
            Tool("sts2_get_legal_actions", "Get only the current state version and legal actions.", new()),
            Tool("sts2_execute_action", "Queue one exact legal action for main-thread execution.", new JsonObject { ["state_version"] = version["state_version"]!.DeepClone(), ["action_id"] = new JsonObject { ["type"] = "string" } }, "state_version", "action_id"),
            Tool("sts2_wait_for_state_change", "Wait until the game publishes a different state version.", new JsonObject { ["state_version"] = version["state_version"]!.DeepClone(), ["timeout_ms"] = new JsonObject { ["type"] = "integer", ["minimum"] = 100, ["maximum"] = 30000 } }, "state_version"),
            Tool("sts2_pause", "Pause bridge action execution and cancel any queued action.", new()),
            Tool("sts2_resume", "Resume bridge action execution.", new()),
            Tool("sts2_get_history", "Get recent queued and execution results.", new JsonObject { ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 200 } }));
    }

    private sealed class RpcException(int code, string message) : Exception(message) { public int Code { get; } = code; }
}
