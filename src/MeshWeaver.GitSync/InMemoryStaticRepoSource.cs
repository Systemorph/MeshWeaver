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
/// <param name="partition">The Space this snapshot materializes into.</param>
/// <param name="children">The parsed repo files as partition child nodes.</param>
/// <param name="root">The parsed partition root, when the repo carries one.</param>
/// <param name="contentSyncs">Git-committed content-collection assets to mirror.</param>
/// <param name="ignore">The Space's gitignore-style sync rules (<see cref="SyncIgnore.For"/>) —
/// the SAME matcher the export and the import already apply, now also applied to the prune via
/// <see cref="IsExcludedFromMirror"/>. Null falls back to <see cref="SyncIgnore.Default"/>, which
/// is what the config-less callers effectively used before.</param>
internal sealed class InMemoryStaticRepoSource(
    string partition,
    IReadOnlyList<MeshNode> children,
    MeshNode? root,
    IReadOnlyList<StaticContentSync>? contentSyncs = null,
    SyncIgnore? ignore = null) : IStaticRepoSource
{
    private readonly SyncIgnore ignoreRules = ignore ?? SyncIgnore.For(null);

    public string Partition => partition;
    public bool Versioned => false;
    public IReadOnlyList<MeshNode> EnumerateSourceNodes() => children;
    public MeshNode? PartitionRoot => root;
    public IReadOnlyList<StaticContentSync> EnumerateInlineContentSyncs() => contentSyncs ?? [];

    /// <summary>
    /// 🚨 The prune's view of the Space's ignore rules (issue #1326). An ignored subtree is
    /// stripped on export and skipped on import, so it is absent from the repo BY DESIGN — the
    /// prune must not read that absence as a deletion. Without this, the default
    /// <c>Release/</c> rule made every mesh-minted release record a permanent prune candidate and
    /// a forced import deleted 47 of them on memex-cloud. The rules match paths relative to the
    /// Space root, exactly as <c>GitHubSyncService.Filter</c> and <c>ParseSnapshot</c> do.
    /// </summary>
    public bool IsExcludedFromMirror(string nodePath)
    {
        if (string.IsNullOrEmpty(nodePath)) return false;
        var prefix = partition + "/";
        var relative = nodePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? nodePath[prefix.Length..]
            : nodePath;
        return ignoreRules.IsIgnored(relative);
    }
}
