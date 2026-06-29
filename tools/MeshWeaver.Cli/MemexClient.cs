using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace MeshWeaver.Cli;

/// <summary>
/// Thin HTTP wrapper around the portal's <c>/api/mesh/*</c> surface. Each method
/// mirrors one REST endpoint (which in turn mirrors one MCP tool).
///
/// <para>All endpoints accept JSON bodies and return JSON strings — the server
/// preserves the <c>MeshWeaver.AI.MeshOperations</c> output verbatim
/// (either a JSON document or an <c>"Error: …"</c> sentinel).</para>
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
    public Task<string> Recycle(string path, CancellationToken ct) => Post("api/mesh/recycle", new { path }, ct);
    public Task<string> Compile(string path, CancellationToken ct) => Post("api/mesh/compile", new { path }, ct);
    public Task<string> Diagnostics(string path, CancellationToken ct) => Post("api/mesh/diagnostics", new { path }, ct);
    public Task<string> ExecuteScript(string path, int timeoutSeconds, CancellationToken ct) => Post("api/mesh/execute-script", new { path, timeoutSeconds }, ct);
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
        if (!resp.IsSuccessStatusCode)
            throw new MemexCliException(resp.StatusCode, body);
        return body;
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
