using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshWeaver.Cli;

/// <summary>
/// The minimal MCP client the CLI needs: JSON-RPC 2.0 over the portal's Streamable HTTP endpoint
/// (<c>{baseUrl}/mcp</c>), authenticated with the same <c>mw_…</c> API token <c>memex login</c>
/// stores. Three methods — <c>initialize</c>, <c>tools/list</c>, <c>tools/call</c> — and nothing
/// else, so the CLI stays a thin client that references no MeshWeaver assembly and no MCP SDK.
///
/// <para>The portal serves <c>/mcp</c> in STATELESS mode (MeshWeaver.Plugins <c>McpExtensions</c>),
/// so every POST is self-contained. The client still runs the spec's handshake once per process —
/// <c>initialize</c>, then the <c>notifications/initialized</c> notification — and carries a
/// server-assigned <c>Mcp-Session-Id</c> if one comes back, so it also works against a stateful
/// server. A reply may arrive as plain JSON or as a <c>text/event-stream</c>; both are read.</para>
/// </summary>
public sealed class McpClient : IDisposable
{
    /// <summary>The protocol revision the client offers; the server's answer is what is sent afterwards.</summary>
    public const string OfferedProtocolVersion = "2025-06-18";

    private readonly HttpClient http;
    private readonly Uri endpoint;
    private string? sessionId;
    private string? protocolVersion;
    private bool initialized;
    private int nextId;

    public McpClient(MemexConfig config)
    {
        endpoint = EndpointFor(config.BaseUrl);
        http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
    }

    /// <summary>The MCP route a portal serves: <c>{baseUrl}/mcp</c>.</summary>
    public static Uri EndpointFor(string baseUrl) => new(baseUrl.TrimEnd('/') + "/mcp");

    /// <summary><c>tools/list</c>, following <c>nextCursor</c> until the catalog is complete.</summary>
    public async Task<JsonArray> ListTools(CancellationToken ct)
    {
        await EnsureInitialized(ct);
        var all = new JsonArray();
        string? cursor = null;
        do
        {
            var parameters = new JsonObject();
            if (cursor is not null) parameters["cursor"] = cursor;
            var result = await Request("tools/list", parameters, ct);
            if (result?["tools"] is JsonArray tools)
                foreach (var tool in tools.ToArray())
                    all.Add(tool?.DeepClone());
            cursor = result?["nextCursor"]?.GetValue<string>();
        }
        while (!string.IsNullOrEmpty(cursor));
        return all;
    }

    /// <summary><c>tools/call</c>; answers the <c>CallToolResult</c> object.</summary>
    public async Task<JsonObject> CallTool(string name, JsonObject arguments, CancellationToken ct)
    {
        await EnsureInitialized(ct);
        var result = await Request("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments }, ct);
        return result as JsonObject ?? throw new McpRpcException(0, $"tools/call '{name}' answered no result object.");
    }

    private async Task EnsureInitialized(CancellationToken ct)
    {
        if (initialized) return;
        var result = await Request("initialize", new JsonObject
        {
            ["protocolVersion"] = OfferedProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "memex-cli", ["version"] = CliVersion() },
        }, ct);
        protocolVersion = result?["protocolVersion"]?.GetValue<string>() ?? OfferedProtocolVersion;
        initialized = true;
        await Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, ct);
    }

    private async Task<JsonNode?> Request(string method, JsonObject parameters, CancellationToken ct)
    {
        var id = ++nextId;
        var body = await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, ct);
        var reply = FindReply(body, id)
                    ?? throw new McpRpcException(0, $"{method}: the server answered no JSON-RPC reply for id {id}.");
        if (reply["error"] is JsonObject error)
            throw new McpRpcException(
                error["code"]?.GetValue<int>() ?? 0,
                error["message"]?.GetValue<string>() ?? error.ToJsonString());
        return reply["result"];
    }

    private async Task<string> Send(JsonObject message, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (sessionId is not null) request.Headers.Add("Mcp-Session-Id", sessionId);
        if (protocolVersion is not null) request.Headers.Add("MCP-Protocol-Version", protocolVersion);

        using var response = await http.SendAsync(request, ct);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids))
            sessionId = ids.FirstOrDefault() ?? sessionId;
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new MemexCliException(response.StatusCode, text);
        return text;
    }

    /// <summary>
    /// The JSON-RPC reply carrying <paramref name="id"/>, from a plain JSON body or from the
    /// <c>data:</c> lines of an event stream (one JSON-RPC message per event).
    /// </summary>
    public static JsonObject? FindReply(string body, int id)
    {
        foreach (var candidate in Messages(body))
            if (candidate is JsonObject obj && obj["id"] is JsonValue v && v.TryGetValue<int>(out var got) && got == id)
                return obj;
        return null;
    }

    private static IEnumerable<JsonNode?> Messages(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            var node = Parse(trimmed);
            if (node is JsonArray batch)
                foreach (var item in batch) yield return item;
            else
                yield return node;
            yield break;
        }

        var data = new StringBuilder();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (data.Length > 0) yield return Parse(data.ToString());
                data.Clear();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart());
            }
        }
        if (data.Length > 0) yield return Parse(data.ToString());
    }

    private static JsonNode? Parse(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static string CliVersion() =>
        typeof(McpClient).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "0.0.0";

    public void Dispose() => http.Dispose();
}

/// <summary>A JSON-RPC <c>error</c> reply: the server understood the request and refused it.</summary>
public sealed class McpRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
