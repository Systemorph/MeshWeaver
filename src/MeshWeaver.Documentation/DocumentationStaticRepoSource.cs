using MeshWeaver.Mesh;

namespace MeshWeaver.Documentation;

/// <summary>
/// The embedded MeshWeaver documentation as a <see cref="IStaticRepoSource"/> — the import source
/// for the <c>Doc</c> partition. The same nodes <c>DocumentationBackfill</c> indexed for search are
/// here materialized (content + prerender) into the partition by the static-repo import on boot, so
/// docs are served from the DB rather than the in-memory embedded adapter. See
/// <c>Doc/Architecture/StaticRepoImport.md</c>.
/// </summary>
public sealed class DocumentationStaticRepoSource : IStaticRepoSource
{
    /// <inheritdoc />
    public string Partition => DocumentationNodeProvider.RootNamespace; // "Doc"

    /// <inheritdoc />
    // Embedded docs ship Versioned=false → fingerprint on content, so an edited .md re-imports.
    public bool Versioned => false;

    /// <inheritdoc />
    public IReadOnlyList<MeshNode> EnumerateSourceNodes() =>
        DocumentationNodeProvider.LoadIndexableNodes();
}
