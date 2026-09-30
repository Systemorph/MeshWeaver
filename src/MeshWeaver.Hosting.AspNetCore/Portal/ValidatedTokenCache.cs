using System.Collections.Immutable;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Hosting.AspNetCore.Portal;

/// <summary>
/// This replica's POSITIVE token verdicts, keyed by the token's SHA-256 hash, for
/// <see cref="Ttl"/>. An MCP client sends the same bearer token on every call — a session is
/// dozens of requests a minute — and each one used to cost a hub round trip that, when it went
/// unanswered, turned into a 60 s wait and a 503 (Doc/Architecture/TokenValidationHotPath).
///
/// <para>🚨 Only SUCCESSES are cached. A negative verdict is never remembered (a token minted a
/// moment ago must not stay "not found"), and neither is UNAVAILABLE (a fault must be re-tried,
/// not replayed).</para>
///
/// <para>The price is revocation latency: a token revoked while cached keeps authenticating on
/// this replica for at most <see cref="Ttl"/>. That is the trade, stated rather than hidden —
/// the same bound the pre-existing <c>UserIdentityCache</c> accepts for identity lookups.</para>
///
/// <para>Bounded at <see cref="Capacity"/> entries: past it, expired entries are dropped first
/// and then the oldest. Immutable snapshot swapped by compare-and-exchange — no locks, no static
/// state; the owner (the middleware instance) holds exactly one.</para>
/// </summary>
public sealed class ValidatedTokenCache
{
    /// <summary>How long a positive verdict is reused.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    /// <summary>The most entries held at once.</summary>
    public const int Capacity = 4096;

    private ImmutableDictionary<string, Entry> entries = ImmutableDictionary<string, Entry>.Empty;

    /// <summary>The number of entries currently held (expired ones included until swept).</summary>
    public int Count => Volatile.Read(ref entries).Count;

    /// <summary>The cached positive verdict for a token hash, or null when absent or expired.</summary>
    /// <param name="tokenHash">The token's SHA-256 hex hash.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The verdict, or null.</returns>
    public ValidateTokenResponse? TryGet(string tokenHash, DateTimeOffset now)
        => Volatile.Read(ref entries).TryGetValue(tokenHash, out var entry) && entry.ExpiresAt > now
            ? entry.Verdict
            : null;

    /// <summary>Remembers a verdict — only when it is a SUCCESS.</summary>
    /// <param name="tokenHash">The token's SHA-256 hex hash.</param>
    /// <param name="verdict">The verdict to remember.</param>
    /// <param name="now">The current instant.</param>
    public void Put(string tokenHash, ValidateTokenResponse verdict, DateTimeOffset now)
    {
        if (!verdict.Success || verdict.IsUnavailable)
            return;
        var entry = new Entry(verdict, now + Ttl);
        while (true)
        {
            var current = Volatile.Read(ref entries);
            var next = Bound(current.SetItem(tokenHash, entry), now);
            if (ReferenceEquals(Interlocked.CompareExchange(ref entries, next, current), current))
                return;
        }
    }

    private static ImmutableDictionary<string, Entry> Bound(ImmutableDictionary<string, Entry> map, DateTimeOffset now)
    {
        if (map.Count <= Capacity)
            return map;
        var live = map.RemoveRange(map.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key));
        return live.Count <= Capacity
            ? live
            : live.RemoveRange(live.OrderBy(kv => kv.Value.ExpiresAt).Take(live.Count - Capacity).Select(kv => kv.Key));
    }

    private sealed record Entry(ValidateTokenResponse Verdict, DateTimeOffset ExpiresAt);
}
