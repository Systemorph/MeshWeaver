using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace MeshWeaver.Hosting;

/// <summary>One NodeType whose content this process could not type, and how often.</summary>
/// <param name="NodeType">The node type (the key the content-type registry recovery ran under).</param>
/// <param name="Seam">The read seam that observed it first.</param>
/// <param name="Count">How many reads degraded.</param>
/// <param name="LastPath">The most recent node path.</param>
/// <param name="LastAt">When.</param>
public sealed record ContentDegradation(
    string NodeType, string Seam, int Count, string? LastPath, DateTimeOffset LastAt);

/// <summary>
/// 🚨 <b>What this replica could not read — kept, so it can be SEEN.</b> A node whose
/// <c>$type</c> resolves to no registered CLR type on the reading hub is degraded to an untyped
/// element and every view of it renders empty (<see cref="Mesh.MeshNodeContentDegradedException"/>).
/// That was a log line and nothing else: on 2026-09-08 both memex replicas served an empty
/// client page for hours while <c>/health</c> answered a bare <c>Degraded</c> that named no cause.
/// The stream cache records every degradation here; the host's <c>ContentTypeHealthCheck</c>
/// (Memex.Portal.ServiceDefaults) reports the set on <c>/health</c> with the node types named.
///
/// <para>A mesh-scoped instance singleton (registered beside the stream cache), never static —
/// its lifetime is the mesh's. Bounded: one entry per node type, counts only.</para>
/// </summary>
public sealed class ContentDegradationRegistry
{
    private readonly ConcurrentDictionary<string, ContentDegradation> byNodeType =
        new(StringComparer.Ordinal);

    /// <summary>Records one degraded read.</summary>
    public void Record(string? nodeType, string? nodePath, string seam)
    {
        var key = string.IsNullOrEmpty(nodeType) ? "(no node type)" : nodeType;
        var now = DateTimeOffset.UtcNow;
        byNodeType.AddOrUpdate(
            key,
            _ => new ContentDegradation(key, seam, 1, nodePath, now),
            (_, existing) => existing with { Count = existing.Count + 1, LastPath = nodePath, LastAt = now });
    }

    /// <summary>Forgets a node type — called when a later read of it typed cleanly, so a
    /// degradation that a module load cured stops being reported.</summary>
    public void Clear(string? nodeType)
    {
        if (!string.IsNullOrEmpty(nodeType))
            byNodeType.TryRemove(nodeType, out _);
    }

    /// <summary>Every node type currently degraded, most recent first.</summary>
    public ImmutableList<ContentDegradation> Snapshot() =>
        byNodeType.Values.OrderByDescending(d => d.LastAt).ToImmutableList();

    /// <summary>True when nothing is degraded.</summary>
    public bool IsEmpty => byNodeType.IsEmpty;

    /// <summary>The check's name on <c>/health</c> (the host registers the check; the sentence
    /// is composed here so a test can pin it without a host).</summary>
    public const string HealthCheckName = "content-types";

    /// <summary>The one sentence an operator reads on <c>/health</c>. Pure.</summary>
    public static string Describe(IReadOnlyList<ContentDegradation> degraded) =>
        degraded.Count == 0
            ? "every node content read on this replica typed"
            : $"{degraded.Count} node type(s) whose content this replica cannot type — the module "
              + "that declares the type is not loaded here (its prebuilt bundle was declined or its "
              + "compiled assembly is not on this replica), so their pages render empty: "
              + string.Join("; ", degraded.Select(d => $"{d.NodeType} ×{d.Count} (last {d.LastPath})"));
}

