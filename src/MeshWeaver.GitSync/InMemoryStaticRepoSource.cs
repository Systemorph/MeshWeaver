using MeshWeaver.Mesh;

namespace MeshWeaver.GitSync;

/// <summary>
/// An <see cref="IStaticRepoSource"/> whose nodes were already fetched + parsed
/// (from a GitHub repo, over HTTP, in the I/O pool) and materialized in memory.
/// Handed to <see cref="MeshWeaver.Graph.StaticRepoImporter.ImportSource"/> so the
/// import reuses the entire documented pipeline — fingerprint gate, content-addressed
/// activity lock, canonical upsert, prune (= mirror) — exactly as the platform
/// static-repo sources do. <see cref="Versioned"/> is false so the fingerprint hashes
/// content (the commit's files), making a re-import at a different commit a new
/// fingerprint that re-syncs and an identical commit a no-op.
///
/// <para>Beyond the node files, the repo's git-committed content-collection assets — the raw
/// binaries under <c>{node}/content/**</c> (course videos/posters, fonts, images) — are captured
/// as <see cref="StaticContentSync"/> groups and returned from
/// <see cref="EnumerateInlineContentSyncs"/>, so the importer MIRRORS them into the owning node's
/// content collection after the node upsert. Binary-safe (bytes travel intact) and mirroring, so
/// committed content lands and stops getting wiped by a re-import.</para>
/// </summary>
internal sealed class InMemoryStaticRepoSource(
    string partition,
    IReadOnlyList<MeshNode> children,
    MeshNode? root,
    IReadOnlyList<StaticContentSync>? contentSyncs = null) : IStaticRepoSource
{
    public string Partition => partition;
    public bool Versioned => false;
    public IReadOnlyList<MeshNode> EnumerateSourceNodes() => children;
    public MeshNode? PartitionRoot => root;
    public IReadOnlyList<StaticContentSync> EnumerateInlineContentSyncs() => contentSyncs ?? [];
}
