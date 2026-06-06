namespace MeshWeaver.Mesh;

/// <summary>
/// A <em>static repository</em> that ships with the build (embedded docs, sample graphs, seed
/// data) and is materialized into its partition by the static-repo import on portal boot. The
/// repo is the <b>source</b>; the live serving copy is the materialized partition. One
/// implementation is registered per partition that should be DB-materialized.
///
/// <para>See <c>Doc/Architecture/StaticRepoImport.md</c>. The importer enumerates
/// <see cref="EnumerateSourceNodes"/>, fingerprints them via
/// <see cref="PartitionSourceFingerprint"/>, and — when the fingerprint differs from the one
/// recorded on the partition main node — materializes the nodes (content + prerender) through the
/// canonical create pipeline, tracked as a content-addressed <c>Activity</c>.</para>
/// </summary>
public interface IStaticRepoSource
{
    /// <summary>
    /// The top-level partition this repo materializes into (the partition namespace and the
    /// partition-root id), e.g. <c>"Doc"</c>. The materialized nodes live under this partition;
    /// the partition main node (<c>namespace="", id={Partition}</c>) records the imported
    /// fingerprint.
    /// </summary>
    string Partition { get; }

    /// <summary>
    /// Whether this repo's nodes carry meaningful versions. Selects the fingerprint mode:
    /// <c>true</c> → <c>(path, version)</c> (cheap); <c>false</c> → <c>(path, contentHash)</c>
    /// (embedded docs ship <c>Versioned=false</c>, so content is what changes).
    /// </summary>
    bool Versioned { get; }

    /// <summary>
    /// All source nodes <b>with full content</b> (e.g. <c>MarkdownContent</c>), in any order
    /// (the fingerprint + import are order-independent). This is the authored content, read
    /// straight from the assembly — never from the live mesh.
    /// </summary>
    IReadOnlyList<MeshNode> EnumerateSourceNodes();
}
