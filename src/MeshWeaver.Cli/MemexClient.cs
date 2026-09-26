using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace MeshWeaver.Cli;

/// <summary>
/// Thin HTTP wrapper around the portal's <c>/api/mesh/*</c> surface. Each method
/// mirrors one REST endpoint (which in turn mirrors one MCP tool).
///
/// <para>All endpoints accept JSON bodies and return JSON strings. A verb's JSON document
/// arrives verbatim with a 200; a <c>MeshWeaver.AI.MeshOperations</c> sentinel sentence
/// (<c>"Error: …"</c>, <c>"Not found: …"</c>, <c>"Unavailable: …"</c>) arrives as a non-2xx
/// whose JSON body is <c>{ "error": &lt;the sentence&gt;, "kind": … }</c> (Plugins#1699). This
/// client unwraps that envelope back to the sentence, so <c>memex</c>'s stdout/stderr and exit
/// codes are what they always were: the sentence verbatim, <c>Error:</c> to stderr with exit 1.
/// Any other non-2xx (401, 404 for an unmapped route, 504) is still a
/// <see cref="MemexCliException"/>.</para>
/// </summary>
public sealed class MemexClient : IDisposable
{
    private readonly HttpClient http;

    public MemexClient(MemexConfig config)
    {
        http = new HttpClient { BaseAddress = new Uri(config.BaseUrl + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        // The session-hub resolver scopes per-caller × Mcp-Session-Id; setting a stable
        // session id keeps consecutive CLI calls on the same hosted hub, so workspace
        // / kernel state is reused across `memex get` → `memex compile` → `memex execute-script`.
        http.DefaultRequestHeaders.Add("Mcp-Session-Id", Environment.MachineName + "-" + Environment.ProcessId);
    }

    public Task<string> Get(string path, CancellationToken ct) => Post("api/mesh/get", new { path }, ct);
    public Task<string> Search(string query, string? basePath, CancellationToken ct) => Post("api/mesh/search", new { query, basePath }, ct);
    public Task<string> Create(string node, CancellationToken ct) => Post("api/mesh/create", new { node }, ct);
    public Task<string> Update(string nodes, CancellationToken ct) => Post("api/mesh/update", new { nodes }, ct);
    public Task<string> Patch(string path, string fields, CancellationToken ct) => Post("api/mesh/patch", new { path, fields }, ct);
    public Task<string> Delete(string paths, CancellationToken ct) => Post("api/mesh/delete", new { paths }, ct);
    public Task<string> Move(string sourcePath, string targetPath, CancellationToken ct) => Post("api/mesh/move", new { sourcePath, targetPath }, ct);
    public Task<string> Copy(string sourcePath, string targetNamespace, bool force, CancellationToken ct) => Post("api/mesh/copy", new { sourcePath, targetNamespace, force }, ct);
    public Task<string> Recycle(string path, CancellationToken ct) => Recycle(path, reason: null, ct);

    /// <summary>
    /// <c>mw recycle &lt;path&gt; [--reason "…"]</c>. An OVERLOAD rather than a defaulted parameter, for
    /// the reason <c>MeshOperations.Recycle</c> states for its own pair: a defaulted parameter
    /// REPLACES the signature an already-built caller compiled against. <c>reason</c> is the
    /// operator's own <i>why</i>, carried into the target's <c>[QUIESCE-START]</c> (MeshWeaver#4782).
    /// </summary>
    public Task<string> Recycle(string path, string? reason, CancellationToken ct) =>
        Post("api/mesh/recycle", new { path, reason }, ct);
    public Task<string> Compile(string path, CancellationToken ct) => Post("api/mesh/compile", new { path }, ct);
    public Task<string> Diagnostics(string path, CancellationToken ct) => Post("api/mesh/diagnostics", new { path }, ct);
    public Task<string> ExecuteScript(string path, int timeoutSeconds, CancellationToken ct) => Post("api/mesh/execute-script", new { path, timeoutSeconds }, ct);
    /// <summary>Runs a node's Tests area as an activity (<c>MeshOperations.RunTests</c>); answers <c>{status, activityPath}</c>.</summary>
    public Task<string> RunTests(string path, int timeoutSeconds, CancellationToken ct) => Post("api/mesh/run-tests", new { path, timeoutSeconds }, ct);
    public Task<string> NavigateTo(string path, CancellationToken ct) => Post("api/mesh/navigate-to", new { path }, ct);
    public Task<string> BaseUrl(CancellationToken ct) => Post("api/mesh/base-url", new { }, ct);

    public Task<string> Mirror(
        string direction,
        string remoteBaseUrl,
        string remoteToken,
        string sourcePath,
        string? targetPath,
        bool removeMissing,
        bool dryRun,
        CancellationToken ct) =>
        Post("api/mesh/mirror", new
        {
            direction,
            remoteBaseUrl,
            remoteToken,
            sourcePath,
            targetPath,
            removeMissing,
            dryRun,
        }, ct);

    public async Task<string> Upload(string path, string localFilePath, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(path), "path");
        await using var fs = File.OpenRead(localFilePath);
        var streamContent = new StreamContent(fs);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(GuessContentType(localFilePath));
        content.Add(streamContent, "file", Path.GetFileName(localFilePath));
        using var resp = await http.PostAsync("api/mesh/upload", content, ct);
        return await ReadBodyAndCheck(resp, ct);
    }

    private async Task<string> Post(string route, object body, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync(route, body, MemexJson.Default, ct);
        return await ReadBodyAndCheck(resp, ct);
    }

    private static async Task<string> ReadBodyAndCheck(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode)
            return body;
        return UnwrapSentinel(body) ?? throw new MemexCliException(resp.StatusCode, body);
    }

    /// <summary>
    /// The sentinel sentence inside a <c>{ "error": …, "kind": … }</c> verb answer, or <c>null</c>
    /// when the body is not that envelope. Recognised by its WIRE shape — a string <c>error</c>
    /// beside a <c>kind</c> naming one of the three sentinels — because this client is
    /// deliberately dependency-free (it links no mesh assembly, so it cannot call
    /// <c>OperationSentinel.Classify</c>); an unrelated <c>{ "error": … }</c> (an unmapped route's
    /// 404, an upload refusal) carries no <c>kind</c> and stays an HTTP failure.
    /// </summary>
    public static string? UnwrapSentinel(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("kind", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || kind.GetString() is not ("Error" or "NotFound" or "Unavailable" or "Refused")
                || !doc.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.String)
                return null;
            return error.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GuessContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".json" => "application/json",
            ".txt" or ".md" => "text/plain",
            ".html" or ".htm" => "text/html",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => "application/octet-stream",
        };

    public void Dispose() => http.Dispose();
}

public sealed class MemexCliException : Exception
{
    public System.Net.HttpStatusCode StatusCode { get; }
    public string Body { get; }
    public MemexCliException(System.Net.HttpStatusCode statusCode, string body)
        : base($"HTTP {(int)statusCode} {statusCode}: {body}")
    {
        StatusCode = statusCode;
        Body = body;
    }
}

internal static class MemexJson
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        // Server endpoints use camelCase for body fields (see MeshApiEndpoints record bodies).
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
