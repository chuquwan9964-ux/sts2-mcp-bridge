using Sts2McpBridge.Core;
using Sts2McpBridge.Server;

int port = int.TryParse(Environment.GetEnvironmentVariable("STS2_MCP_PORT"), out int configuredPort) && configuredPort is > 0 and <= 65535 ? configuredPort : 37845;
bool daemon = args.Contains("--daemon", StringComparer.Ordinal);
string token = TokenProvider.LoadOrCreate();
BridgeStore store = new(token);
string knowledgeRoot = Environment.GetEnvironmentVariable("STS2_KNOWLEDGE_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "sts2-knowledge", "spire-codex", "zhs");
KnowledgeStore knowledge = new(knowledgeRoot);
await using HttpBridgeServer http = new(store, port);
http.Start();
using HttpBridgeApi bridge = new(new Uri($"http://127.0.0.1:{port}/"), token);
McpServer mcp = new(bridge, knowledge);
Console.Error.WriteLine($"STS2 MCP Bridge listening on 127.0.0.1:{port}; token source is configured.");

using CancellationTokenSource lifetime = new();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; lifetime.Cancel(); };
if (daemon)
{
    try { await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token); }
    catch (OperationCanceledException) { }
    return;
}
while (!lifetime.IsCancellationRequested)
{
    string? line;
    try { line = await Console.In.ReadLineAsync(lifetime.Token); }
    catch (OperationCanceledException) { break; }
    if (line is null) break;
    string? response = await mcp.HandleLineAsync(line, lifetime.Token);
    if (response is not null)
    {
        await Console.Out.WriteLineAsync(response);
        await Console.Out.FlushAsync();
    }
}
