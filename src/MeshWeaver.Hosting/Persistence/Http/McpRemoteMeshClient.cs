using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
/// SDK is Task-based, so each HTTP/MCP round-trip is bridged through the shared
/// <see cref="IIoPool"/> (the <c>Http</c> pool) — never a bare
/// <c>Observable.FromAsync</c> — so the remote wait runs off the calling hub
/// scheduler (the deadlock fix) and is bounded. See ControlledIoPooling.md.</para>
///
/// <para>The <see cref="McpClient"/> connect/handshake runs <b>exactly once</b>
/// via the <see cref="IIoPool"/> <i>promise-cache</i>: the first caller kicks the
/// connect off on the pool, every later caller composes off the same cached
/// <see cref="ReplaySubject{T}"/>-backed observable (<see cref="IoPoolExtensions.Run{T}"/>)
/// and replays the one connected client — the textbook replacement for a
/// <c>SemaphoreSlim(1,1)</c> connect gate. Disposed on <see cref="DisposeAsync"/>.
/// Reusable for the lifetime of one mirror operation.</para>
/// </summary>
public sealed class McpRemoteMeshClient : IRemoteMeshClient, IAsyncDisposable
{
    private readonly Uri _endpoint;
    private readonly string _remoteToken;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILoggerFactory _loggerFactory;

    // Http I/O pool — every CallToolAsync leaf AND the one-shot connect handshake
    // route through it so the remote HTTP wait runs off the calling hub scheduler
    // (the deadlock fix) and is bounded. Falls back to the stateless unbounded pool
    // (still offloads) when constructed without a registry. See ControlledIoPooling.md.
    private readonly IIoPool _pool;

    // Promise-cache: the connect handshake observable, created lazily on the first
    // call and shared by every later caller. pool.Run(...) is eager + ReplaySubject-
    // backed (IoPoolExtensions.Run): it runs the connect ONCE on the Http pool and
    // replays the resulting client to all subscribers — so the connect happens
    // exactly once under concurrency with no SemaphoreSlim gate. The _connectLock
    // guards only the field assignment (a synchronous, never-awaited lazy init), so
    // pool.Run is never invoked twice; it never parks the action block.
    private readonly object _connectLock = new();
    private IObservable<McpClient>? _connect;

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
        Connect().SelectMany(client =>
            _pool.Invoke(async ct =>
            {
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
            }));

    public IObservable<Unit> Create(MeshNode node) =>
        Connect().SelectMany(client =>
            _pool.Invoke(async ct =>
            {
                var nodeJson = JsonSerializer.Serialize(node, _jsonOptions);
                var result = await client.CallToolAsync(
                    "create",
                    new Dictionary<string, object?> { ["node"] = nodeJson }!,
                    progress: null, options: null, cancellationToken: ct).ConfigureAwait(false);
                ThrowIfError(result, $"create({node.Path})");
                return Unit.Default;
            }));

    public IObservable<Unit> Update(MeshNode node) =>
        Connect().SelectMany(client =>
            _pool.Invoke(async ct =>
            {
                var nodesJson = JsonSerializer.Serialize(new[] { node }, _jsonOptions);
                var result = await client.CallToolAsync(
                    "update",
                    new Dictionary<string, object?> { ["nodes"] = nodesJson }!,
                    progress: null, options: null, cancellationToken: ct).ConfigureAwait(false);
                ThrowIfError(result, $"update({node.Path})");
                return Unit.Default;
            }));

    public IObservable<Unit> Delete(string path) =>
        Connect().SelectMany(client =>
            _pool.Invoke(async ct =>
            {
                var result = await client.CallToolAsync(
                    "delete",
                    new Dictionary<string, object?> { ["path"] = "@" + path }!,
                    progress: null, options: null, cancellationToken: ct).ConfigureAwait(false);
                ThrowIfError(result, $"delete({path})");
                return Unit.Default;
            }));

    public IObservable<IReadOnlyList<string>> SearchPaths(string query) =>
        Connect().SelectMany(client =>
            _pool.Invoke(async ct =>
            {
                var result = await client.CallToolAsync(
                    "search",
                    new Dictionary<string, object?> { ["query"] = query }!,
                    progress: null, options: null, cancellationToken: ct).ConfigureAwait(false);
                var text = ExtractText(result);
                if (string.IsNullOrEmpty(text)) return (IReadOnlyList<string>)Array.Empty<string>();

                // Search returns a JSON array of anonymous objects each with a `path` field.
                var jsonNode = JsonNode.Parse(text);
                if (jsonNode is not JsonArray arr) return Array.Empty<string>();
                var paths = new List<string>(arr.Count);
                foreach (var item in arr)
                {
                    if (item is JsonObject obj && obj["path"]?.GetValue<string>() is { Length: > 0 } p)
                        paths.Add(p);
                }
                return (IReadOnlyList<string>)paths;
            }));

    /// <summary>
    /// The connect/handshake promise — opens the <see cref="McpClient"/> exactly
    /// once on the Http <see cref="IIoPool"/> and replays the connected client to
    /// every caller. The lock guards only the lazy field assignment (synchronous,
    /// never awaited); <c>pool.Run</c> being eager + ReplaySubject-backed means the
    /// handshake runs a single time even under concurrent first calls.
    /// </summary>
    private IObservable<McpClient> Connect()
    {
        if (_connect is { } existing) return existing;
        lock (_connectLock)
        {
            return _connect ??= _pool.Run(ConnectAsync);
        }
    }

    private async Task<McpClient> ConnectAsync(CancellationToken ct)
    {
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
        return await McpClient.CreateAsync(
            transport, clientOptions: null, _loggerFactory, ct).ConfigureAwait(false);
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
        // Snapshot + clear the connect promise so no leaf composes off a disposing
        // client. The cached observable is ReplaySubject-backed, so this Take(1)
        // replays the already-connected client (no re-connect) if it ran.
        IObservable<McpClient>? connect;
        lock (_connectLock)
        {
            connect = _connect;
            _connect = null;
        }
        if (connect is null) return;

        McpClient? client = null;
        try
        {
            // The connect already ran (or is running) on the pool; observe its single
            // result to dispose it. No re-connect — the ReplaySubject replays.
            client = await connect.FirstOrDefaultAsync().ToTask().ConfigureAwait(false);
        }
        catch { /* connect failed — nothing to dispose */ }

        if (client is not null)
        {
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow — adapter cleanup is best-effort */ }
        }
    }
}
