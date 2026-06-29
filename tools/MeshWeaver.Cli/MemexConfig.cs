using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.Cli;

/// <summary>
/// Connection settings for the <c>memex</c> CLI. Resolved in priority order:
/// command-line flag → env var → <c>~/.memex/config.json</c> → built-in default
/// (for <c>BaseUrl</c> only — token has no default).
/// </summary>
public sealed class MemexConfig
{
    private const string DefaultBaseUrl = "https://memex.meshweaver.cloud";
    private const string EnvBaseUrl = "MEMEX_BASE_URL";
    private const string EnvToken = "MEMEX_TOKEN";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public required string BaseUrl { get; init; }
    public required string Token { get; init; }

    public static MemexConfig Resolve(string? flagBaseUrl, string? flagToken)
    {
        var file = ReadConfigFile();
        var baseUrl = Pick(flagBaseUrl, Environment.GetEnvironmentVariable(EnvBaseUrl), file?.BaseUrl, DefaultBaseUrl)!;
        var token = Pick(flagToken, Environment.GetEnvironmentVariable(EnvToken), file?.Token, null);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException(
                $"No API token configured. Set it with --token, ${EnvToken}, or `memex login`.");
        return new MemexConfig { BaseUrl = baseUrl.TrimEnd('/'), Token = token };
    }

    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".memex", "config.json");

    public static void SaveFile(string? baseUrl, string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var file = new ConfigFile { BaseUrl = baseUrl, Token = token };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(file, Json));
    }

    private static ConfigFile? ReadConfigFile()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            using var s = File.OpenRead(ConfigPath);
            return JsonSerializer.Deserialize<ConfigFile>(s, Json);
        }
        catch { return null; }
    }

    private static string? Pick(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c)) return c;
        return null;
    }

    public sealed class ConfigFile
    {
        [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }
        [JsonPropertyName("token")] public string? Token { get; set; }
    }
}
