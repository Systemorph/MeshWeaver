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
    /// The partition's <see cref="PartitionSyncMode"/> — what the import PRUNES after upserting this
    /// source's nodes. Defaults to <see cref="PartitionSyncMode.FullReplace"/> (mirror the partition to
    /// the repo), which is the pre-existing behavior for every source that does not opt in. The built-in
    /// AI catalogs (Skill / Agent / Provider / Harness) override to <see cref="PartitionSyncMode.Additive"/>
    /// so a user's own skills/agents survive re-import. Independent of the per-node
    /// <see cref="SyncBehavior"/>, which still claims/protects individual nodes in any mode.
    /// </summary>
    PartitionSyncMode SyncMode => PartitionSyncMode.FullReplace;

    /// <summary>
    /// All source nodes <b>with full content</b> (e.g. <c>MarkdownContent</c>), in any order
    /// (the fingerprint + import are order-independent). This is the authored content, read
    /// straight from the assembly — never from the live mesh.
    ///
    /// <para>These are the partition's <b>children</b> only — never the partition root. The
    /// <c>namespace=""</c> root is created as a proper <c>Space</c> by the importer as a standard
    /// step (see <see cref="PartitionRoot"/>); shipping it here would break the importer's
    /// descendants-scoped existing-read and prune logic.</para>
    /// </summary>
    IReadOnlyList<MeshNode> EnumerateSourceNodes();

    /// <summary>
    /// Optional customization of the partition <b>root</b> node (<c>namespace="", id={Partition}</c>).
    /// The importer always ensures a proper <c>Space</c> root exists for the partition as a standard
    /// step; when this returns <c>null</c> it synthesizes a generic <c>Space</c> root. Override it to
    /// ship a curated landing page (e.g. the Doc welcome page) — typically
    /// <c>new MeshNode(Partition) { NodeType = "Space", Content = new MarkdownContent { … } }</c>.
    ///
    /// <para>Use <c>NodeType = "Space"</c> as a string and <c>MarkdownContent</c> for the body — do
    /// NOT reference the <c>Space</c> record (it lives in the portal assembly). The importer
    /// prerenders the markdown into <c>PreRenderedHtml</c>, which the Space Overview renders.</para>
    /// </summary>
    MeshNode? PartitionRoot => null;

    /// <summary>
    /// Optional content-collection <b>imports</b> — assets a node references through its content
    /// collection (e.g. an <c>@@content/logo.svg</c> embed) that ship in an embedded source
    /// collection (e.g. <c>DocContent</c>) and must be copied into the owning node's runtime content
    /// collection. After the node upsert the importer posts an <see cref="ImportContentRequest"/> per
    /// entry (under System) to the owning node's hub, which copies the source folder
    /// collection→collection, stream-to-stream. Default empty.
    /// </summary>
    IReadOnlyList<StaticContentImport> EnumerateContentImports() => [];

    /// <summary>
    /// Optional content-collection <b>files carried inline</b> (their raw bytes travel with the source,
    /// not via an embedded source collection) that must be MIRRORED into a node's content collection.
    /// This is how a GitSync import syncs the git-committed <c>{Space}/content/**</c> binaries (course
    /// videos/posters) into the mesh: the fetch captures each blob's bytes, and after the node upsert
    /// the importer posts a <see cref="SyncContentFilesRequest"/> per group (under System) to the owning
    /// node's hub, which writes the bytes AND — mirroring — deletes files under the folder the group no
    /// longer carries. Binary-safe (bytes never round-trip through a text/JSON string) and idempotent.
    /// Default empty. Independent of <see cref="EnumerateContentImports"/> (collection→collection copies).
    /// </summary>
    IReadOnlyList<StaticContentSync> EnumerateInlineContentSyncs() => [];
}

/// <summary>
/// A content-collection MIRROR shipped inline by an <see cref="IStaticRepoSource"/>: write
/// <paramref name="Files"/> into <paramref name="TargetCollection"/>/<paramref name="TargetPath"/> on
/// the node at <paramref name="NodePath"/>, pruning any file the folder still has that the set no longer
/// carries. The bytes travel with the source (binary-safe). Maps onto <see cref="SyncContentFilesRequest"/>.
/// </summary>
/// <param name="NodePath">The full path of the node whose hub owns the target collection (the Space root for the per-Space <c>content</c> collection).</param>
/// <param name="Files">The files to mirror, each path relative to <paramref name="TargetPath"/>.</param>
/// <param name="TargetCollection">The target content collection (default <c>content</c>).</param>
/// <param name="TargetPath">The folder within the collection to mirror the files under (empty for the collection root).</param>
public record StaticContentSync(
    string NodePath,
    IReadOnlyList<InlineContentFile> Files,
    string TargetCollection = "content",
    string TargetPath = "");

/// <summary>
/// A content-collection import shipped by an <see cref="IStaticRepoSource"/>: copy the
/// <paramref name="SourcePath"/> folder of <paramref name="SourceCollection"/> into
/// <paramref name="TargetCollection"/>/<paramref name="TargetPath"/> on the node at
/// <paramref name="NodePath"/>. Maps directly onto <see cref="ImportContentRequest"/>.
/// </summary>
public record StaticContentImport(
    string NodePath,
    string SourceCollection,
    string SourcePath,
    string TargetCollection = "content",
    string TargetPath = "");
