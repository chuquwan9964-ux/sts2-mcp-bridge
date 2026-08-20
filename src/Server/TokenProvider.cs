using System.Security.Cryptography;

namespace Sts2McpBridge.Server;

public static class TokenProvider
{
    public static string LoadOrCreate()
    {
        string? environmentToken = Environment.GetEnvironmentVariable("STS2_MCP_TOKEN");
        if (!string.IsNullOrWhiteSpace(environmentToken)) return environmentToken;

        string path = ResolvePath();
        if (File.Exists(path))
        {
            string stored = File.ReadAllText(path).Trim();
            if (stored.Length < 16) throw new InvalidOperationException("STS2 MCP token file contains an invalid token.");
            return stored;
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(path, generated + Environment.NewLine);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return generated;
    }

    public static string ResolvePath()
    {
        string? configured = Environment.GetEnvironmentVariable("STS2_MCP_TOKEN_FILE");
        if (!string.IsNullOrWhiteSpace(configured)) return ExpandUser(configured);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile)) throw new InvalidOperationException("Cannot resolve the user profile for the default token file.");
        return Path.Combine(profile, ".config", "sts2-mcp-bridge", "token");
    }

    private static string ExpandUser(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.Length == 1 ? profile : Path.Combine(profile, path[2..]);
        }
        return Path.GetFullPath(path);
    }
}
