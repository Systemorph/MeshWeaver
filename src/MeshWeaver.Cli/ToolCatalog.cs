using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshWeaver.Cli;

/// <summary>One MCP tool as <c>tools/list</c> describes it.</summary>
public sealed record McpTool(string Name, string? Description, JsonObject InputSchema)
{
    /// <summary>Reads one entry of a <c>tools/list</c> answer; <c>null</c> when it carries no name.</summary>
    public static McpTool? From(JsonNode? node)
    {
        if (node is not JsonObject obj || obj["name"]?.GetValue<string>() is not { Length: > 0 } name)
            return null;
        var schema = obj["inputSchema"] as JsonObject ?? new JsonObject();
        return new McpTool(name, obj["description"]?.GetValue<string>(), schema);
    }
}

/// <summary>
/// The tool catalog the dynamic commands are generated from, and where it came from.
/// </summary>
/// <param name="Tools">The tools, in the order the server listed them.</param>
/// <param name="FetchedAt">When the catalog was read from the server.</param>
/// <param name="FromCache">True when the catalog was served from the local cache.</param>
public sealed record ToolCatalog(IReadOnlyList<McpTool> Tools, DateTimeOffset FetchedAt, bool FromCache);

/// <summary>The outcome of loading the catalog: a catalog, a warning to print, or the reason there is none.</summary>
public sealed record CatalogLoad(ToolCatalog? Catalog, string? Warning, string? Failure);

/// <summary>
/// Loads the connected instance's MCP tool catalog, caching it per instance URL under the CLI's
/// config directory (<c>~/.memex/tools/</c>).
///
/// <para>A cache younger than <see cref="Lifetime"/> is used without contacting the server;
/// <c>--refresh</c> skips it. When the server cannot be read, ANY cached catalog for that URL is
/// used and a warning says how old it is; with no cache the load fails, naming why — the CLI then
/// has no mesh commands to offer, and says so instead of reporting an unknown command.</para>
/// </summary>
public static class ToolCatalogCache
{
    /// <summary>How long a cached catalog is used without asking the server again.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The cache file for one instance URL: readable host name plus a hash of the full URL.</summary>
    public static string CachePath(string cacheDirectory, string baseUrl)
    {
        var normalized = baseUrl.TrimEnd('/').ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
        var host = Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ? uri.Host : "instance";
        var safeHost = new string(host.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());
        return Path.Combine(cacheDirectory, "tools", $"{safeHost}-{hash}.json");
    }

    /// <summary>
    /// Loads the catalog for <paramref name="baseUrl"/>. <paramref name="fetch"/> reads
    /// <c>tools/list</c>; it is <c>null</c> when no client can be built (no token), which counts as
    /// "the server cannot be read".
    /// </summary>
    public static async Task<CatalogLoad> Load(
        string cacheDirectory,
        string baseUrl,
        bool refresh,
        DateTimeOffset now,
        Func<CancellationToken, Task<JsonArray>>? fetch,
        string? noFetchReason,
        CancellationToken ct)
    {
        var path = CachePath(cacheDirectory, baseUrl);
        var cached = Read(path);
        if (!refresh && cached is not null && now - cached.FetchedAt < Lifetime)
            return new CatalogLoad(cached, null, null);

        string reason;
        if (fetch is null)
        {
            reason = noFetchReason ?? "no client could be built";
        }
        else
        {
            try
            {
                var raw = await fetch(ct);
                var catalog = new ToolCatalog(raw.Select(McpTool.From).OfType<McpTool>().ToArray(), now, false);
                Write(path, baseUrl, raw, now);
                return new CatalogLoad(catalog, null, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
            }
        }

        if (cached is not null)
            return new CatalogLoad(cached,
                $"warning: could not read the tool catalog from {baseUrl.TrimEnd('/')}/mcp ({reason}); "
                + $"using the catalog cached at {cached.FetchedAt:u}.", null);

        return new CatalogLoad(null, null,
            $"error: could not read the tool catalog from {baseUrl.TrimEnd('/')}/mcp ({reason}), "
            + "and no cached catalog exists for this instance. Check --base-url and the token "
            + "(`memex login`), then retry.");
    }

    private static ToolCatalog? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject doc) return null;
            if (doc["fetchedAt"] is not JsonValue stamp || !stamp.TryGetValue<string>(out var text)
                || !DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var fetchedAt)
                || doc["tools"] is not JsonArray raw)
                return null;
            return new ToolCatalog(raw.Select(McpTool.From).OfType<McpTool>().ToArray(), fetchedAt, true);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // An unreadable cache (corrupt, or access denied) is no cache: the next successful fetch overwrites it.
            return null;
        }
    }

    private static void Write(string path, string baseUrl, JsonArray raw, DateTimeOffset now)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var doc = new JsonObject
            {
                ["baseUrl"] = baseUrl,
                ["fetchedAt"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["tools"] = raw.DeepClone(),
            };
            var temp = path + ".tmp";
            File.WriteAllText(temp, doc.ToJsonString(Indented));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
            // A cache that cannot be written costs a round trip next time, never the command.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
