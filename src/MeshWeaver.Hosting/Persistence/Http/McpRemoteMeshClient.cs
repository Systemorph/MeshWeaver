using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MeshWeaver.Hosting.Persistence.Http;

/// <summary>
/// Production <see cref="IRemoteMeshClient"/>: lazily opens an
/// <see cref="McpClient"/> against a remote MeshWeaver portal's <c>/mcp</c>
/// endpoint and forwards each call as a <c>CallToolAsync</c> on the standard
/// `get` / `create` / `update` / `delete` / `search` tools the
/// <c>McpMeshPlugin</c> already exposes.
///
/// <para>Auth is the receiving portal's <c>ApiTokenAuthenticationHandler</c>:
/// we send <c>Authorization: Bearer {remoteToken}</c> as an additional header on
/// every transport request.</para>
///
/// <para><b>Observable surface:</b> per the AsynchronousCalls.md convention every
/// public method returns <see cref="IObservable{T}"/>. The <see cref="McpClient"/>
/// SDK is Task-based, so each method bridges once via
/// <see cref="Observable.FromAsync{TResult}(System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task{TResult}})"/>
/// at the leaf — no nested awaits, no scheduler-bridging continuations.</para>
///
/// <para>The <see cref="McpClient"/> is opened on first call and disposed on
/// <see cref="DisposeAsync"/>. Reusable for the lifetime of one mirror operation.</para>
/// </summary>
public sealed class McpRemoteMeshClient : IRemoteMeshClient, IAsyncDisposable
{
    private readonly Uri _endpoint;
    private readonly string _remoteToken;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILoggerFactory _loggerFactory;

    private McpClient? _client;
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    // Http I/O pool — every CallToolAsync leaf routes through it so the remote
    // HTTP wait runs off the calling hub scheduler (the deadlock fix) and is
    // bounded. Falls back to the stateless unbounded pool (still offloads) when
    // constructed without a registry. See ControlledIoPooling.md.
    private readonly IIoPool _pool;

    public McpRemoteMeshClient(
        string remoteBaseUrl,
        string remoteToken,
        JsonSerializerOptions jsonOptions,
        ILoggerFactory? loggerFactory = null,
        IIoPool? pool = null)
    {
        if (string.IsNullOrWhiteSpace(remoteBaseUrl))
            throw new ArgumentException("Remote base URL is required.", nameof(remoteBaseUrl));
        if (string.IsNullOrWhiteSpace(remoteToken))
            throw new ArgumentException("Remote ApiToken is required.", nameof(remoteToken));

        _endpoint = new Uri(new Uri(remoteBaseUrl.TrimEnd('/') + "/"), "mcp");
        _remoteToken = remoteToken;
        _jsonOptions = jsonOptions;
        _loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        _pool = pool ?? IoPool.Unbounded;
    }

    public IObservable<MeshNode?> Get(string path) =>
        _pool.Invoke(async ct =>
        {
            var client = await GetClientAsync(ct).ConfigureAwait(false);
            var result = await client.CallToolAsync(
                "get",
                new Dictionary<string, object?> { ["path"] = "@" + path }!,
                progress: null, options: null, cancellationToken: ct).ConfigureAwait(false);

            var text = ExtractText(result);
            // The `get` tool returns the literal string "Not found: <path>" rather than
            // an MCP error when the node doesn't exist. Treat that as null.
            if (string.IsNullOrEmpty(text) || text.StartsWith("Not found", StringComparison.OrdinalIgnoreCase))
                return (MeshNode?)null;
            return JsonSerializer.Deserialize<MeshNode>(text, _jsonOptions);
        });

    public IObservable<Unit> Create(MeshNode node) =>
        _pool.Invoke(async ct =>
        {
            var client = await GetClientAsync(ct).ConfigureAwait(false);
            var nodeJson = JsonSerializer.Serialize(node, _jsonOptions);
            var result = await client.CallToolAsync(
                "create",
                new Dictionary<string, object?> { ["node"] = nodeJson }!,
                progress: null, options: null, cancellationToken: ct).ConfigureAwait(false);
            ThrowIfError(result, $"create({node.Path})");
            return Unit.Default;
        });

    public IObservable<Unit> Update(MeshNode node) =>
        _pool.Invoke(async ct =>
        {
            var client = await GetClientAsync(ct).ConfigureAwait(false);
            var nodesJson = JsonSerializer.Serialize(new[] { node }, _jsonOptions);
            var result = await client.CallToolAsync(
                "update",
                new Dictionary<string, object?> { ["nodes"] = nodesJson }!,
                progress: null, options: null, cancellationToken: ct).ConfigureAwait(false);
            ThrowIfError(result, $"update({node.Path})");
            return Unit.Default;
        });

    public IObservable<Unit> Delete(string path) =>
        _pool.Invoke(async ct =>
        {
            var client = await GetClientAsync(ct).ConfigureAwait(false);
            var result = await client.CallToolAsync(
                "delete",
                new Dictionary<string, object?> { ["path"] = "@" + path }!,
                progress: null, options: null, cancellationToken: ct).ConfigureAwait(false);
            ThrowIfError(result, $"delete({path})");
            return Unit.Default;
        });

    public IObservable<IReadOnlyList<string>> SearchPaths(string query) =>
        _pool.Invoke(async ct =>
        {
            var client = await GetClientAsync(ct).ConfigureAwait(false);
            var result = await client.CallToolAsync(
                "search",
                new Dictionary<string, object?> { ["query"] = query }!,
                progress: null, options: null, cancellationToken: ct).ConfigureAwait(false);
            var text = ExtractText(result);
            if (string.IsNullOrEmpty(text)) return (IReadOnlyList<string>)Array.Empty<string>();

            // Search returns a JSON array of anonymous objects each with a `path` field.
            var node = JsonNode.Parse(text);
            if (node is not JsonArray arr) return Array.Empty<string>();
            var paths = new List<string>(arr.Count);
            foreach (var item in arr)
            {
                if (item is JsonObject obj && obj["path"]?.GetValue<string>() is { Length: > 0 } p)
                    paths.Add(p);
            }
            return (IReadOnlyList<string>)paths;
        });

    private async Task<McpClient> GetClientAsync(CancellationToken ct)
    {
        if (_client is not null) return _client;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null) return _client;

            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + _remoteToken
            };
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = _endpoint,
                    Name = "MeshWeaver Mirror",
                    AdditionalHeaders = headers,
                },
                _loggerFactory);
            _client = await McpClient.CreateAsync(
                transport, clientOptions: null, _loggerFactory, ct).ConfigureAwait(false);
            return _client;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private static string ExtractText(CallToolResult result)
    {
        if (result.IsError == true)
            ThrowMcpError(result, operation: "callTool");
        if (result.Content is null) return string.Empty;
        // The MCP tools we call return a single text block. Grab the first one;
        // if there are multiple, concatenate.
        var text = string.Empty;
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock t)
                text += t.Text;
        }
        return text;
    }

    private static void ThrowIfError(CallToolResult result, string operation)
    {
        if (result.IsError == true)
            ThrowMcpError(result, operation);
    }

    private static void ThrowMcpError(CallToolResult result, string operation)
    {
        var detail = string.Empty;
        if (result.Content is not null)
        {
            foreach (var block in result.Content)
                if (block is TextContentBlock t) detail += t.Text;
        }
        throw new InvalidOperationException(
            $"Remote MeshWeaver MCP call failed ({operation}): " +
            (string.IsNullOrEmpty(detail) ? "(no error detail returned)" : detail));
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is { } c)
        {
            try { await c.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow — adapter cleanup is best-effort */ }
            _client = null;
        }
        _connectGate.Dispose();
    }
}
