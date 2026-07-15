using MeshWeaver.Mesh;

namespace MeshWeaver.GitSync;

/// <summary>
/// Maps a repo's git-committed <b>content-collection assets</b> — the raw files under a node's
/// <c>content/</c> folder (<c>{node}/content/**</c>: course videos, posters, fonts, images) — onto the
/// mesh's per-Space <c>content</c> collection, so a GitSync import can MIRROR them in (the fix for
/// content that was never synced from git and so "disappeared" on every sync).
///
/// <para>The per-Space <c>content</c> collection is mounted on the partition ROOT (children inherit it)
/// and served at <c>/static/storage/content/{nodePath}/{file}</c> — i.e. a <c>content:</c> reference on
/// node <c>{Space}/A/B</c> resolves under collection-relative path <c>A/B/{file}</c>. So a repo file
/// (subdirectory-relative, i.e. relative to the Space) is a content asset iff it has a path segment
/// exactly <c>content</c>; the segments BEFORE it are the owning node's Space-relative path (which is
/// also the collection sub-folder), and the segments AFTER it are the file's path within that folder.</para>
///
/// <list type="bullet">
///   <item><c>content/videos/x.mp4</c> → owner = Space root, folder = <c>""</c>, file = <c>videos/x.mp4</c>.</item>
///   <item><c>TDD/content/x.png</c> → owner = <c>{Space}/TDD</c>, folder = <c>TDD</c>, file = <c>x.png</c>.</item>
/// </list>
/// The NEAREST <c>content</c> segment wins (the first one), so a nested <c>A/content/B/content/y</c> maps
/// to owner <c>{Space}/A</c>, file <c>B/content/y</c> — the deeper "content" is just a folder name.
/// </summary>
public static class ContentAssetMapper
{
    /// <summary>The reserved folder segment that marks a node's content-collection subtree.</summary>
    public const string ContentSegment = "content";

    /// <summary>
    /// A single classified content asset: which node owns it, where it lives within the content
    /// collection, and its bytes.
    /// </summary>
    /// <param name="OwnerRelativePath">The owning node's path relative to the Space (empty for the Space root).</param>
    /// <param name="FileRelativePath">The file's path within the owning node's content folder (e.g. <c>videos/x.mp4</c>).</param>
    /// <param name="Bytes">The file's raw bytes.</param>
    public record ContentAsset(string OwnerRelativePath, string FileRelativePath, byte[] Bytes);

    /// <summary>
    /// Cheap path-only precheck: could <paramref name="repoRelativePath"/> be a content asset at all?
    /// True iff it has a <c>content</c> path segment that is not the last segment. Lets a caller skip
    /// materializing a file's bytes for the common case (a node file, never under <c>content/</c>).
    /// </summary>
    public static bool IsContentPath(string repoRelativePath)
        => TrySplit(repoRelativePath, out _, out _);

    /// <summary>
    /// Classifies a subdirectory-relative repo path as a content asset, or returns null when it is NOT
    /// under any node's <c>content/</c> folder (so it flows on to node parsing). A path whose FIRST
    /// segment is <c>content</c> belongs to the Space root; a deeper <c>content</c> segment names the
    /// owning node's folder. A path that is exactly <c>content</c> or ends right at a <c>content</c>
    /// segment (no file after it) is not an asset. The <paramref name="bytes"/> factory is invoked
    /// ONLY when the path actually classifies — so a large repo pays no per-file byte cost for its
    /// (majority) node files.
    /// </summary>
    public static ContentAsset? TryClassify(string repoRelativePath, Func<byte[]> bytes)
        => TrySplit(repoRelativePath, out var owner, out var file)
            ? new ContentAsset(owner, file, bytes())
            : null;

    /// <summary>Path-only classification: splits a content path into (owner, file) or returns false.</summary>
    private static bool TrySplit(string repoRelativePath, out string owner, out string file)
    {
        owner = string.Empty;
        file = string.Empty;
        if (string.IsNullOrEmpty(repoRelativePath))
            return false;
        var segments = repoRelativePath.Replace('\\', '/').Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Find the FIRST "content" segment — but never at the very end (no file follows it).
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!string.Equals(segments[i], ContentSegment, StringComparison.OrdinalIgnoreCase))
                continue;
            var f = string.Join('/', segments.Skip(i + 1));
            if (f.Length == 0)
                return false;
            owner = string.Join('/', segments.Take(i));
            file = f;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Builds the SINGLE whole-collection <see cref="StaticContentSync"/> for a Space, ready for
    /// <see cref="IStaticRepoSource.EnumerateInlineContentSyncs"/>. The per-Space <c>content</c>
    /// collection is ONE physical store (mounted on the Space root, children inherit it), so the mirror
    /// must be ONE authoritative pass over the whole collection against the FULL repo content set — not
    /// per-owner passes, which would overlap (the root owner's pass recurses into a module owner's
    /// folder and would prune it). Each asset keeps its full collection-relative path
    /// <c>{owner}/{file}</c> (matching the served <c>content:</c> URL on the owning node), and
    /// <c>TargetPath</c> is empty (the collection root). Returns an empty list when there are no assets
    /// (so a repo carrying zero content leaves the collection completely untouched — no empty mirror
    /// that would wipe a manually-uploaded file the repo simply doesn't track).
    /// </summary>
    public static IReadOnlyList<StaticContentSync> ToContentSyncs(
        string spaceId, IEnumerable<ContentAsset> assets, string collectionName = "content")
    {
        var files = assets
            .Select(a => new InlineContentFile(
                a.OwnerRelativePath.Length == 0 ? a.FileRelativePath : $"{a.OwnerRelativePath}/{a.FileRelativePath}",
                a.Bytes))
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
        return files.Length == 0
            ? []
            : [new StaticContentSync(spaceId, files, collectionName, TargetPath: "")];
    }
}
