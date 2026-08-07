using System.Reflection;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// A BUILD ASSET published on the portal's public <c>/static</c> route (issue #587).
///
/// <para>🚨 <c>/static</c> carries application build output ONLY — the files that ship inside a
/// MeshWeaver assembly (icon SVGs, the shipped documentation's images, scripts, fonts). It performs
/// NO permission check of any kind, resolves NO identity, and never touches the mesh: a mount reads
/// its bytes straight out of <see cref="Assembly.GetManifestResourceStream(string)"/>. That is the
/// whole point — everything served there is public by construction, so responses stay
/// <c>public, immutable</c> and CDN-cacheable.</para>
///
/// <para><b>What must NEVER become a mount.</b> Anything sourced from a content collection, a
/// partition, user storage, a synced content repo or an upload. Those are mesh content; they are
/// served by the authenticated <c>/api/content/{node}/{collection}/{file}</c> route, whose
/// <c>GetDataRequest</c> carries <c>[RequiresPermission(Read)]</c> and is evaluated by the owning
/// node's hub. Before this existed, <c>/static/storage/content/{node}/{file}</c> read the
/// mesh-level backing store directly and every partition's uploads were world-readable at a fully
/// predictable URL.</para>
///
/// <para>Registered as a plain singleton on the mesh's service collection; the hosting layer
/// resolves <c>IEnumerable&lt;StaticAssetMount&gt;</c> and serves them. A path whose first segment
/// matches no mount is 404 — for every caller alike, because being unmounted is a hosting decision
/// and never an access decision.</para>
/// </summary>
/// <param name="Segment">
/// The first path segment the mount answers to — <c>/static/{Segment}/{file}</c>. Matched
/// case-insensitively.
/// </param>
/// <param name="Assembly">The assembly whose embedded resources back the mount.</param>
/// <param name="ResourcePrefix">
/// The manifest-resource-name prefix that scopes the mount (e.g. <c>MeshWeaver.Graph.Icons</c>).
/// A file path is mapped onto it exactly as <see cref="EmbeddedResourceStreamProvider"/> does:
/// <c>/</c> becomes <c>.</c> and the result is appended to the prefix.
/// </param>
public sealed record StaticAssetMount(string Segment, Assembly Assembly, string ResourcePrefix)
{
    /// <summary>
    /// Opens a file within the mount, or returns <c>null</c> when it does not exist or the path is
    /// not safe to resolve.
    ///
    /// <para>The path is validated with <see cref="IsSafeRelativePath"/> BEFORE it is mapped to a
    /// resource name. A manifest-resource lookup cannot escape the assembly the way a file-system
    /// <c>Path.Combine</c> can, but the guard is kept here too so a traversal attempt is refused at
    /// the boundary rather than relying on a downstream accident.</para>
    /// </summary>
    /// <param name="relativePath">The slash-style file path within the mount (e.g. <c>box.svg</c>).</param>
    /// <returns>The resource stream, or <c>null</c>.</returns>
    public Stream? Open(string? relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
            return null;
        var prefix = ResourcePrefix.EndsWith('.') ? ResourcePrefix : ResourcePrefix + '.';
        return Assembly.GetManifestResourceStream(prefix + relativePath!.Replace('/', '.'));
    }

    /// <summary>
    /// Rejects a path whose segments are unsafe to resolve: an empty segment (<c>//</c>), a dot
    /// segment (<c>.</c> / <c>..</c>), a backslash, or an embedded NUL.
    ///
    /// <para>🚨 This runs on the DECODED path. ASP.NET Core normalizes <c>..</c> out of the request
    /// line, but the catch-all route value is still percent-encoded, so <c>%2E%2E</c> survives
    /// normalization and only becomes <c>..</c> when the endpoint un-escapes it. Validating before
    /// decoding would therefore pass a traversal straight through.</para>
    /// </summary>
    /// <param name="relativePath">A decoded path or path fragment.</param>
    /// <returns><c>true</c> when every segment is safe.</returns>
    public static bool IsSafeRelativePath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return false;
        if (relativePath.Contains('\\') || relativePath.Contains('\0'))
            return false;
        foreach (var segment in relativePath.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
                return false;
        }
        return true;
    }
}
