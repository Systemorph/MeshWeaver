using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace MeshWeaver.Data.Serialization;

/// <summary>
/// The SPLICE encoding for a changed string leaf inside a cross-hub RFC&#160;7396 merge patch
/// (<c>PatchDataRequest</c>), plus the compact BASE FINGERPRINT that keeps the writer's
/// three-way-merge signal for that leaf O(1) instead of O(length).
///
/// <para><b>Why.</b> A cross-hub <c>stream.Update</c> ships the changed leaf's WHOLE new value and
/// — via <see cref="MeshNodePatchMerge.ExtractBaseValues"/> — its WHOLE old value. For a field that
/// GROWS one chunk at a time (an agent response cell streaming tokens at
/// <c>Sample(100&#160;ms)</c>) that makes the round's wire cost
/// <c>O(ticks × final length)</c>: a 20&#160;kB answer re-ships ~20&#160;kB twice per tick, megabytes
/// for one reply, every byte of it a fresh Large-Object-Heap allocation on the owner and on the
/// writer. Satellites cannot fix it — a streaming cell must show all the text so far — but the
/// PATCH does not have to carry text the owner already has. Encoding the leaf as a splice makes the
/// write <c>O(chunk)</c>, and it does so for EVERY large string field, not just chat.</para>
///
/// <para><b>Wire shape.</b> Two objects, each a single reserved <c>$</c>-prefixed key so they can
/// never be confused with domain content:</para>
/// <list type="bullet">
///   <item><description>in the patch — <c>{ "$sd": [start, removedLength, "inserted"] }</c>;</description></item>
///   <item><description>in <c>BaseValues</c> at the same leaf —
///     <c>{ "$sdb": [baseLength, "fingerprint"] }</c>.</description></item>
/// </list>
///
/// <para><b>Correctness.</b> The owner applies the splice at its literal offsets ONLY when the
/// fingerprint proves its live text is byte-identical to the text the writer diffed against — then
/// the result is provably the same string the full-value patch would have written. When the
/// fingerprint does NOT match, the owner has moved since the writer's base and the splice's offsets
/// no longer address the text they were computed for, so the leaf is REFUSED (the owner keeps its
/// newer value and NACKs <c>Conflict</c>, which re-runs the writer's update lambda against the fresh
/// state and re-diffs). Refusing is the conservative half of the trade: a splice is never applied at
/// an unverified offset, so two mirrors splicing the same string concurrently can never corrupt it.
/// </para>
///
/// <para>Distinct from <see cref="StringDeltaPatch"/>, which encodes the same splice for the
/// <c>EntityDeltaUpdate</c> sync-stream path and replays it onto the target's CURRENT text with no
/// base signal at all. Both use the same <c>$sd</c> marker on purpose — one shape, one meaning.</para>
/// </summary>
public static class PatchStringSplice
{
    /// <summary>Marker key of a spliced string leaf. Shared with <see cref="StringDeltaPatch"/>.</summary>
    public const string Marker = StringDeltaPatch.Marker;

    /// <summary>Marker key of the compact base fingerprint carried in <c>BaseValues</c>.</summary>
    public const string BaseMarker = "$sdb";

    /// <summary>
    /// Below this length the whole value is cheaper than a splice plus a fingerprint, and the
    /// three-way merge keeps its full-fidelity string rebase. Only big string fields — markdown
    /// bodies, prerendered html, a streaming agent response — take the splice path. Matches
    /// <see cref="EntityDelta.MinDeltaSize"/>, the same threshold the sync-stream delta uses.
    /// </summary>
    public const int MinSpliceLength = EntityDelta.MinDeltaSize;

    // Wire cost of the wrapper itself: {"$sd":[NNNNN,NNNNN,""]} plus JSON escaping headroom.
    // Used only to reject a "splice" that would be no smaller than the value it replaces.
    private const int MarkerOverhead = 48;

    // 96 bits of SHA-256, base64. Paired with an exact length check, so a false "unchanged since
    // base" verdict needs a same-length preimage collision — far below every other failure mode
    // on this path, and unlike a non-cryptographic hash it is not craftable from user content.
    private const int FingerprintBytes = 12;

    /// <summary>
    /// Encodes <paramref name="oldValue"/> → <paramref name="newValue"/> as a splice, when that is
    /// worth doing: the new value is at least <see cref="MinSpliceLength"/> long, the strings really
    /// differ, and the encoded splice is smaller than the value it replaces. Returns <c>false</c>
    /// (and leaves <paramref name="encoded"/> null) otherwise, so the caller ships the full value
    /// with unchanged semantics.
    /// </summary>
    public static bool TryEncode(string? oldValue, string? newValue, out JsonObject? encoded)
    {
        encoded = null;
        if (newValue is null || newValue.Length < MinSpliceLength)
            return false;
        var delta = StringDelta.Compute(oldValue, newValue);
        if (delta.IsEmpty)
            return false;
        if (delta.Inserted.Length + MarkerOverhead >= newValue.Length)
            return false; // a near-total rewrite — the full value is no bigger and keeps full fidelity
        encoded = new JsonObject
        {
            [Marker] = new JsonArray(delta.Start, delta.RemovedLength, delta.Inserted)
        };
        return true;
    }

    /// <summary>Builds the compact base-fingerprint node for a spliced leaf.</summary>
    public static JsonObject EncodeBase(string? baseValue)
    {
        baseValue ??= string.Empty;
        return new JsonObject
        {
            [BaseMarker] = new JsonArray(baseValue.Length, Fingerprint(baseValue))
        };
    }

    /// <summary>
    /// Recognises a spliced string leaf. Deliberately strict — exactly one property, exactly the
    /// marker, exactly <c>[int, int, string]</c> with non-negative offsets — so an ordinary content
    /// object can never be mistaken for one.
    /// </summary>
    public static bool TryDecode(JsonNode? node, out StringDelta delta)
    {
        delta = default;
        if (node is not JsonObject obj || obj.Count != 1)
            return false;
        if (!obj.TryGetPropertyValue(Marker, out var value) || value is not JsonArray { Count: 3 } arr)
            return false;
        if (arr[0] is not JsonValue s || !s.TryGetValue<int>(out var start) || start < 0)
            return false;
        if (arr[1] is not JsonValue r || !r.TryGetValue<int>(out var removed) || removed < 0)
            return false;
        if (arr[2] is not JsonValue i || !i.TryGetValue<string>(out var inserted))
            return false;
        delta = new StringDelta(start, removed, inserted);
        return true;
    }

    /// <summary>Recognises the compact base fingerprint written by <see cref="EncodeBase"/>.</summary>
    public static bool TryDecodeBase(JsonNode? node, out int length, out string fingerprint)
    {
        length = 0;
        fingerprint = string.Empty;
        if (node is not JsonObject obj || obj.Count != 1)
            return false;
        if (!obj.TryGetPropertyValue(BaseMarker, out var value) || value is not JsonArray { Count: 2 } arr)
            return false;
        if (arr[0] is not JsonValue l || !l.TryGetValue<int>(out var len) || len < 0)
            return false;
        if (arr[1] is not JsonValue f || !f.TryGetValue<string>(out var fp))
            return false;
        length = len;
        fingerprint = fp;
        return true;
    }

    /// <summary>
    /// True when <paramref name="live"/> is the same text the writer diffed against — the ONLY
    /// condition under which a splice may be applied at its literal offsets.
    /// </summary>
    public static bool BaseMatches(string? live, int length, string fingerprint)
    {
        live ??= string.Empty;
        return live.Length == length
               && string.Equals(Fingerprint(live), fingerprint, StringComparison.Ordinal);
    }

    /// <summary>Stable content fingerprint of a string (truncated SHA-256 over its UTF-16 bytes).</summary>
    public static string Fingerprint(string? value)
    {
        value ??= string.Empty;
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(MemoryMarshal.AsBytes(value.AsSpan()), hash);
        return Convert.ToBase64String(hash[..FingerprintBytes]);
    }
}
