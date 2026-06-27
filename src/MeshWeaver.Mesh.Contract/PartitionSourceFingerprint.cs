using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeshWeaver.Mesh;

/// <summary>
/// Deterministic, order-independent <em>content-version</em> hash over a static repo's node set —
/// the idempotency key for the static-repo import pattern. The fingerprint changes iff a node is
/// <b>added, removed, or modified</b>, so it is the natural answer to "has this exact content
/// already been imported?" and the natural id for the content-addressed import <c>Activity</c>.
///
/// <para>The import stamps the result on the partition main node (<c>ImportedSourceHash</c>) and
/// embeds it in the activity path (<c>{Partition}/_Activity/import-{fingerprint}</c>). See
/// <c>Doc/Architecture/StaticRepoImport.md</c>.</para>
/// </summary>
public static class PartitionSourceFingerprint
{
    /// <summary>
    /// Fingerprint over explicit <c>(path, token)</c> entries — the caller decides what each
    /// node's token captures (a version, a content hash, …). Entries are sorted by path before
    /// hashing, so <b>enumeration order does not affect the result</b>. Empty/blank paths are
    /// ignored.
    /// </summary>
    public static string Compute(IEnumerable<(string Path, string Token)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var sb = new StringBuilder();
        foreach (var (path, token) in entries
                     .Where(e => !string.IsNullOrEmpty(e.Path))
                     .OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            // Length-prefix framing → injective, so no separator char is needed and no
            // (path, token) pair can collide with a differently-split one.
            AppendFramed(sb, path);
            AppendFramed(sb, token ?? string.Empty);
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Fingerprint over <see cref="MeshNode"/>s. A <paramref name="versioned"/> partition uses
    /// <c>(path, version)</c> — cheap, no content read. An unversioned static repo (embedded docs,
    /// <c>Versioned=false</c>) falls back to a per-node content hash over the stable display +
    /// content fields, serialised with <paramref name="contentOptions"/> (pass the hub's
    /// polymorphism-aware options so typed <c>Content</c> serialises stably).
    /// </summary>
    public static string Compute(
        IEnumerable<MeshNode> nodes,
        bool versioned,
        JsonSerializerOptions? contentOptions = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return Compute(nodes.Select(n => (
            n.Path,
            versioned
                ? n.Version.ToString(CultureInfo.InvariantCulture)
                : NodeContentToken(n, contentOptions))));
    }

    /// <summary>
    /// A stable per-node token for the unversioned case: ALL source-meaningful properties plus the
    /// serialised <see cref="MeshNode.Content"/>, SHA-256'd. Capturing every source prop (not just the
    /// display fields) means ANY authored change re-imports — e.g. stamping <see cref="MeshNode.Order"/>
    /// on the harness nodes now changes the fingerprint, so existing deployments pick the fix up on the
    /// next deploy instead of silently keeping the stale import.
    ///
    /// <para>Deliberately NOT <c>node.GetHashCode()</c>: a record's hash folds <c>string.GetHashCode()</c>
    /// (per-process randomised since .NET Core) and hashes collection props by reference, so it differs
    /// every process — a non-deterministic fingerprint would re-import on every pod/restart. We hash a
    /// DETERMINISTIC serialisation instead. Runtime/audit fields (CreatedDate/By, LastModified/By,
    /// Version) and the un-serialisable <c>Func</c> config fields (HubConfiguration,
    /// GlobalServiceConfigurations) are EXCLUDED — they are not source content. The derived
    /// <c>PreRenderedHtml</c> cache is excluded for the same reason (it is computed from Content).
    /// Length-prefix framing keeps the concatenation injective.</para>
    /// </summary>
    private static string NodeContentToken(MeshNode node, JsonSerializerOptions? options)
    {
        var contentJson = node.Content is null
            ? string.Empty
            : JsonSerializer.Serialize(node.Content, options ?? DefaultContentOptions);
        var sb = new StringBuilder();
        AppendFramed(sb, node.Name ?? string.Empty);
        AppendFramed(sb, node.Description ?? string.Empty);
        AppendFramed(sb, node.Category ?? string.Empty);
        AppendFramed(sb, node.Icon ?? string.Empty);
        AppendFramed(sb, node.NodeType ?? string.Empty);
        AppendFramed(sb, node.MainNode ?? string.Empty);
        AppendFramed(sb, node.Order?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        AppendFramed(sb, node.State.ToString());
        AppendFramed(sb, node.SyncBehavior.ToString());
        AppendFramed(sb, node.IsDefinitionOnly ? "1" : "0");
        AppendFramed(sb, node.IsSatelliteType ? "1" : "0");
        AppendFramed(sb, node.DesiredId ?? string.Empty);
        AppendFramed(sb, node.ExcludeFromContext is null ? string.Empty : string.Join(",", node.ExcludeFromContext));
        AppendFramed(sb, contentJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Appends <paramref name="value"/> as <c>{byteLength}:{value}</c> — injective framing.</summary>
    private static void AppendFramed(StringBuilder sb, string value)
        => sb.Append(Encoding.UTF8.GetByteCount(value)).Append(':').Append(value);

    // Immutable default options for the content-serialisation fallback — a read-only constant, not a
    // mutable cache (NoStaticState.md permits static-readonly immutable lookups). Callers that need
    // polymorphic fidelity pass the hub's JsonSerializerOptions explicitly.
    private static readonly JsonSerializerOptions DefaultContentOptions = new();
}
