namespace Sts2McpBridge;

public sealed record BridgeConfig(Uri ServerUrl, string Token)
{
    public static BridgeConfig FromEnvironment()
    {
        string rawUrl = Environment.GetEnvironmentVariable("STS2_MCP_URL") ?? "http://127.0.0.1:37845";
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? url) || url.Scheme != Uri.UriSchemeHttp || !IsLoopback(url.Host))
            throw new InvalidOperationException("STS2_MCP_URL must be an http://127.0.0.1 or http://localhost URL.");
        string? token = Environment.GetEnvironmentVariable("STS2_MCP_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            string path = ResolveTokenPath();
            if (!File.Exists(path)) throw new InvalidOperationException($"Token file does not exist: {path}. Start the MCP server first or set STS2_MCP_TOKEN.");
            token = File.ReadAllText(path).Trim();
        }
        if (token.Length < 16) throw new InvalidOperationException("STS2 MCP token is invalid.");
        return new(url, token);
    }

    private static bool IsLoopback(string host) => host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    private static string ResolveTokenPath()
    {
        string? configured = Environment.GetEnvironmentVariable("STS2_MCP_TOKEN_FILE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (configured == "~" || configured.StartsWith("~/", StringComparison.Ordinal))
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return configured.Length == 1 ? profile : Path.Combine(profile, configured[2..]);
            }
            return Path.GetFullPath(configured);
        }
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".config", "sts2-mcp-bridge", "token");
    }
}
