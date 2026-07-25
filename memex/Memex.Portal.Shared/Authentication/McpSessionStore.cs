using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Mesh-backed store for stateful MCP Streamable-HTTP session state, so a session established
/// on one portal replica can be migrated to any other.
///
/// <para>
/// Each session's <c>InitializeRequestParams</c> (serialised) is a MeshNode at
/// <c>Admin/McpSession/{hashPrefix}</c> — Admin partition, regular <c>mesh_nodes</c> table
/// (PG-persisted, shared by every replica), exactly like <see cref="OAuthCodeStore"/>. This
/// is what makes MCP sessions <b>replica-safe</b>: the MCP client carries no affinity cookie
/// (unlike the Blazor browser), so with more than one silo a follow-up request for an
/// established <c>Mcp-Session-Id</c> can land on a replica that never served the
/// <c>initialize</c> and be rejected 404 ("Session not found"). With this store,
/// <see cref="McpSessionMigrationHandler"/> re-hydrates the session on whichever replica
/// receives the request.
/// </para>
///
/// <para>
/// The raw session id is never persisted — the node id is the first 12 chars of the id's
/// SHA-256 hash and the content carries the full hash (same scheme as
/// <see cref="OAuthCodeStore"/> / <see cref="ApiTokenService"/>). All nodes live in the Admin
/// partition, so every mesh operation runs under the System identity
/// (<see cref="AccessService.ImpersonateAsSystem"/>).
/// 🚨 No async/await/Task in this file — the surface is <see cref="IObservable{T}"/>
/// end-to-end; <see cref="McpSessionMigrationHandler"/> bridges at the transport boundary only.
/// </para>
/// </summary>
internal class McpSessionStore(IMeshService meshService, IMessageHub hub)
{
    private const string NodeTypeMcpSession = "McpSession";
    private const string SessionNamespace = "Admin/McpSession";
    private const int HashPrefixLength = 12;

    /// <summary>
    /// Bounded read-your-writes wait for the session node to become readable on the replica
    /// that receives the migrating request. A stored session resolves in ~1–2 s (Take(1) on
    /// the first matching emission); an unknown id never matches and falls through to this
    /// timeout → null → the SDK returns 404 and the client re-initializes. Generous because
    /// the owning per-node hub may cold-activate from Postgres on a different silo. Init-only
    /// so tests can shorten it.
    /// </summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A stored session may be migrated up to this long after its last write. A healthy session
    /// caches in-memory on each replica after one migration, so this only bounds resuming a
    /// session that idled out of memory. Init-settable so tests can pin the expiry branch
    /// (a zero lifetime makes every stored session already expired) instead of sleeping.
    /// </summary>
    internal TimeSpan SessionLifetime { get; init; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Persists a session's initialize params as a mesh node under the System identity. Cold
    /// observable — the write happens on Subscribe and emits once the create commits, so a
    /// later request on ANY replica can re-hydrate it. Idempotent: a repeat of the same session
    /// id (a rare re-initialize) is treated as success rather than a create conflict.
    /// </summary>
    public IObservable<bool> StoreSession(string sessionId, string owner, string initializeParamsJson)
    {
        var hash = HashSessionId(sessionId);
        var entry = new McpSessionEntry
        {
            SessionIdHash = hash,
            Owner = owner,
            InitializeParamsJson = initializeParamsJson,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var node = new MeshNode(hash[..HashPrefixLength], SessionNamespace)
        {
            Name = $"MCP session {hash[..HashPrefixLength]}",
            NodeType = NodeTypeMcpSession,
            State = MeshNodeState.Active,
            Content = entry,
        };

        return AsSystem(() => meshService.CreateNode(node))
            .Select(_ => true)
            // A duplicate id (re-initialize of the same session) is fine — the stored entry is
            // equivalent; only a genuine infrastructure failure should surface.
            .Catch<bool, Exception>(ex => IsAlreadyExists(ex)
                ? Observable.Return(true)
                : Observable.Throw<bool>(ex));
    }

    /// <summary>
    /// Reads the stored session by id (System identity). Emits the entry, or null when no live,
    /// unexpired session node exists for the id (unknown / expired). Tolerates the
    /// create→persist→routable lag via the same resilient <c>GetMeshNodeStream</c> poll
    /// <see cref="OAuthCodeStore"/> uses at /token.
    /// </summary>
    public IObservable<McpSessionEntry?> ReadSession(string sessionId)
    {
        var hash = HashSessionId(sessionId);
        var path = PathForSession(sessionId);

        return AsSystem(() => ReadSessionNode(path, hash))
            .Select(node =>
            {
                var entry = ExtractEntry(node);
                if (entry is null)
                    return null;
                if (DateTimeOffset.UtcNow - entry.CreatedAt > SessionLifetime)
                    return null;
                return entry;
            });
    }

    /// <summary>
    /// Reads the session node by exact path, tolerating the create→persist→routable lag: a
    /// freshly-created node is not instantly readable across a multi-silo boundary. Each attempt
    /// swallows the transient "no node found" and, only on no-match, re-subscribes after 50 ms —
    /// <c>Concat</c>, never <c>Merge</c>, so exactly ONE owner-hub subscription is live at a time.
    /// A missing id keeps re-probing until <see cref="ReadTimeout"/> → null; a stored session
    /// emits on the first matching attempt and the outer <c>Take(1)</c> tears the chain down.
    /// (Same shape as <c>OAuthCodeStore.ReadCodeNode</c>.)
    /// </summary>
    private IObservable<MeshNode?> ReadSessionNode(string path, string hash)
    {
        IObservable<MeshNode?> Attempt() =>
            hub.GetWorkspace().GetMeshNodeStream(path)
                .Take(1)
                .Where(n => ExtractEntry(n) is { } e
                            && string.Equals(e.SessionIdHash, hash, StringComparison.Ordinal))
                .Select(n => (MeshNode?)n)
                .Catch<MeshNode?, Exception>(_ => Observable.Empty<MeshNode?>())
                .Concat(Observable.Defer(Attempt).DelaySubscription(TimeSpan.FromMilliseconds(50)));

        return Observable.Defer(Attempt)
            .Take(1)
            .Timeout(ReadTimeout)
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null));
    }

    /// <summary>
    /// Runs <paramref name="inner"/> under the System identity — session nodes live in the Admin
    /// partition, which the calling end-user has no rights on. Subscribe-time scope, same shape
    /// as <see cref="OAuthCodeStore"/>. Null <see cref="AccessService"/> (bare unit-test DI)
    /// falls through to the caller's ambient identity.
    /// </summary>
    private IObservable<T> AsSystem<T>(Func<IObservable<T>> inner)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return accessService is null
            ? Observable.Defer(inner)
            : Observable.Using(() => accessService.ImpersonateAsSystem(), _ => inner());
    }

    private static bool IsAlreadyExists(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string HashSessionId(string sessionId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId))).ToLowerInvariant();

    /// <summary>The mesh path a session's node lives at — exposed for tests.</summary>
    internal static string PathForSession(string sessionId)
        => $"{SessionNamespace}/{HashSessionId(sessionId)[..HashPrefixLength]}";

    /// <summary>Node content → <see cref="McpSessionEntry"/>, with a JsonElement fallback for
    /// hubs that have not registered the type / older persisted rows (same as OAuthCodeStore).</summary>
    private static McpSessionEntry? ExtractEntry(MeshNode? node)
    {
        switch (node?.Content)
        {
            case McpSessionEntry direct:
                return direct;
            case System.Text.Json.JsonElement jsonElement:
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<McpSessionEntry>(
                        jsonElement.GetRawText(),
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    return null;
                }
            default:
                return null;
        }
    }
}

/// <summary>
/// Persisted content of an <c>Admin/McpSession/{hashPrefix}</c> node. Carries the full SHA-256
/// hash of the <c>Mcp-Session-Id</c> (never the raw id), the authenticated owner (so only the
/// original caller may re-bind the session on another replica), and the session's
/// <c>InitializeRequestParams</c> serialised as JSON (the MCP SDK owns that type; the store
/// keeps it opaque).
/// </summary>
internal record McpSessionEntry
{
    public required string SessionIdHash { get; init; }
    public required string Owner { get; init; }
    public required string InitializeParamsJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
