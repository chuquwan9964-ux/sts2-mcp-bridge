using System.Net;
using System.Text.Json;
using Sts2McpBridge.Core;

namespace Sts2McpBridge.Server;

public sealed class HttpBridgeServer(BridgeStore store, int port) : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;

    public void Start()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _loop = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                HttpListenerContext context = await _listener.GetContextAsync().WaitAsync(_lifetime.Token);
                _ = HandleSafelyAsync(context);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (HttpListenerException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task HandleSafelyAsync(HttpListenerContext context)
    {
        try { await HandleAsync(context); }
        catch (JsonException) { await WriteAsync(context.Response, 400, new { error = "Invalid JSON request." }); }
        catch (Exception exception) { await WriteAsync(context.Response, 500, new { error = exception.Message }); }
        finally { context.Response.Close(); }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath ?? string.Empty;
        string method = context.Request.HttpMethod;
        if (method == "PUT" && path == "/v1/register")
        {
            RegisterRequest request = await ReadAsync<RegisterRequest>(context.Request);
            if (!store.Authenticate(request.Token)) { await Unauthorized(context.Response); return; }
            store.Register(request.State);
            await WriteAsync(context.Response, 200, new { registered = true });
            return;
        }
        if (method == "POST" && path == "/v1/action")
        {
            ActionRequest request = await ReadAsync<ActionRequest>(context.Request);
            if (!store.Authenticate(request.Token)) { await Unauthorized(context.Response); return; }
            ActionResponse result = store.Queue(request);
            await WriteAsync(context.Response, result.Accepted ? 202 : 409, result);
            return;
        }
        if (!store.Authenticate(ReadHeaderToken(context.Request))) { await Unauthorized(context.Response); return; }
        if (method == "GET" && path == "/v1/state")
        {
            BridgeState? state = store.GetState();
            await WriteAsync(context.Response, state is null ? 503 : 200, state ?? (object)new { error = "No game state is registered." });
        }
        else if (method == "GET" && path == "/v1/action/pending")
        {
            if (!long.TryParse(context.Request.QueryString["state_version"], out long version)) { await WriteAsync(context.Response, 400, new { error = "state_version is required." }); return; }
            PendingAction? pending = store.TakePending(version);
            await WriteAsync(context.Response, 200, new { action = pending });
        }
        else if (method == "POST" && path == "/v1/action/result")
        {
            ActionResultRequest result = await ReadAsync<ActionResultRequest>(context.Request);
            store.RecordResult(new(result.StateVersion, result.ActionId), result.Status);
            await WriteAsync(context.Response, 200, new { recorded = true });
        }
        else if (method == "POST" && path is "/v1/pause" or "/v1/resume")
        {
            bool paused = path == "/v1/pause";
            store.SetPaused(paused);
            await WriteAsync(context.Response, 200, new { paused });
        }
        else if (method == "GET" && path == "/v1/history")
        {
            int limit = int.TryParse(context.Request.QueryString["limit"], out int value) ? value : 50;
            await WriteAsync(context.Response, 200, new { history = store.GetHistory(limit) });
        }
        else await WriteAsync(context.Response, 404, new { error = "Not found." });
    }

    private static string? ReadHeaderToken(HttpListenerRequest request)
    {
        string? authorization = request.Headers["Authorization"];
        return authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? authorization[7..] : request.Headers["X-Sts2-Mcp-Token"];
    }

    private static async Task<T> ReadAsync<T>(HttpListenerRequest request) =>
        await JsonSerializer.DeserializeAsync<T>(request.InputStream, BridgeJson.Options) ?? throw new JsonException("Missing request body.");

    private static Task Unauthorized(HttpListenerResponse response) => WriteAsync(response, 401, new { error = "Unauthorized." });

    private static async Task WriteAsync(HttpListenerResponse response, int status, object value)
    {
        response.StatusCode = status;
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.OutputStream, value, BridgeJson.Options);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Close();
        if (_loop is not null) await _loop;
        _lifetime.Dispose();
    }

    private sealed record ActionResultRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("state_version")] long StateVersion,
        [property: System.Text.Json.Serialization.JsonPropertyName("action_id")] string ActionId,
        [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status);
}
