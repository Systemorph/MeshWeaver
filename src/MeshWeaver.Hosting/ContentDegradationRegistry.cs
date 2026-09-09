using System.Collections.Concurrent;
using System.Collections.Immutable;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting;

/// <summary>One NodeType whose content this process could not type, and how often.</summary>
/// <param name="NodeType">The node type (the key the content-type registry recovery ran under).</param>
/// <param name="Seam">The read seam that observed it first.</param>
/// <param name="Count">How many reads degraded.</param>
/// <param name="LastPath">The most recent node path.</param>
/// <param name="LastAt">When.</param>
/// <param name="Discriminator">The stored <c>$type</c>, when the degraded content carried one — the
/// second key <see cref="ContentDegradationRegistry.Unresolved"/> re-asks the content-type registry
/// under, for content whose NodeType is absent or unregistered.</param>
public sealed record ContentDegradation(
    string NodeType, string Seam, int Count, string? LastPath, DateTimeOffset LastAt,
    string? Discriminator = null);

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
    /// <param name="nodeType">The node's NodeType.</param>
    /// <param name="nodePath">The node path.</param>
    /// <param name="seam">The read seam that observed it.</param>
    /// <param name="discriminator">The content's stored <c>$type</c>, when it carried one. Kept so
    /// <see cref="Unresolved"/> can re-ask the registry by NAME as well as by NodeType — the same
    /// two routes <c>TryRecoverForNodeType</c> takes.</param>
    public void Record(string? nodeType, string? nodePath, string seam, string? discriminator = null)
    {
        var key = string.IsNullOrEmpty(nodeType) ? "(no node type)" : nodeType;
        var now = DateTimeOffset.UtcNow;
        byNodeType.AddOrUpdate(
            key,
            _ => new ContentDegradation(key, seam, 1, nodePath, now, discriminator),
            (_, existing) => existing with
            {
                Count = existing.Count + 1,
                LastPath = nodePath,
                LastAt = now,
                // First-seen wins: a later read of the same NodeType whose element happens to carry
                // no $type must not erase the name the FIRST one gave us to re-ask under.
                Discriminator = existing.Discriminator ?? discriminator,
            });
    }

    /// <summary>Forgets a node type — called when a later read of it typed cleanly, so a
    /// degradation that a module load cured stops being reported.</summary>
    public void Clear(string? nodeType)
    {
        if (!string.IsNullOrEmpty(nodeType))
            byNodeType.TryRemove(nodeType, out _);
    }

    /// <summary>
    /// 🚨 <b>The verdict, as opposed to the event (#3645).</b> The entries whose content type is
    /// STILL unresolvable — re-asked against <paramref name="contentTypes"/> at the moment of the
    /// call, rather than believed from when the read happened.
    ///
    /// <para>A degradation is recorded at the INSTANT of a read, and at that instant nothing can
    /// know whether the type will register a moment later. That is the ordinary state during a
    /// portal boot — a NodeType's runtime compile lands after the first readers are served — and
    /// #2952 exists precisely to re-type those readers when the registration arrives. So a
    /// snapshot of what degraded answers <i>"content was unreadable at a read"</i>, while the
    /// question every consumer of this registry actually asks (<c>/health</c>, and the CI gate) is
    /// <i>"content is unreadable"</i>.</para>
    ///
    /// <para>Both routes <c>TryRecoverForNodeType</c> takes are re-asked, and nothing else: the
    /// EXACT route on the node's own NodeType, and the NAME route on the stored <c>$type</c>. Both
    /// are pure map lookups, so this needs no content and can be called on any thread, as often as
    /// a health probe likes.</para>
    ///
    /// <para>🚨 <b>A null registry means UNRESOLVED, never resolved.</b> There is no registry to
    /// clear an entry with, so the honest answer is the one that keeps reporting — a missing
    /// instrument may not read as a clean result.</para>
    /// </summary>
    /// <param name="contentTypes">The mesh-wide content-type registry, or <c>null</c>.</param>
    /// <returns>The still-unresolvable degradations, most recent first.</returns>
    public ImmutableList<ContentDegradation> Unresolved(IMeshContentTypeRegistry? contentTypes) =>
        Snapshot().Where(d => !IsResolvable(d, contentTypes)).ToImmutableList();

    /// <summary>Whether one recorded degradation's type resolves NOW. Pure given the registry.</summary>
    private static bool IsResolvable(ContentDegradation degradation, IMeshContentTypeRegistry? contentTypes)
    {
        if (contentTypes is null)
            return false;
        if (!string.IsNullOrEmpty(degradation.NodeType)
            && contentTypes.TryResolveByNodeType(degradation.NodeType, out _))
            return true;
        return !string.IsNullOrEmpty(degradation.Discriminator)
               && contentTypes.TryResolveByDiscriminator(degradation.Discriminator!, out _);
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

