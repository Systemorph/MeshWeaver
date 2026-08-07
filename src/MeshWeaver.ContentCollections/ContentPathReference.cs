namespace MeshWeaver.ContentCollections;

/// <summary>
/// Reads a stored, user-authored reference to a file in a content collection back into the
/// <c>{collection}/{path}</c> form a server-side reader needs (branding logos, export templates).
///
/// <para>Three shapes are accepted, and they must stay accepted together:</para>
/// <list type="bullet">
/// <item><c>content:{collection}/{path}</c> — the canonical authored form.</item>
/// <item><c>/api/content/{collection}/content/{path}</c> — the ACCESS-CONTROLLED content-route URL
///   the product now renders and a user may paste back in.</item>
/// <item><c>/static/storage/content/{collection}/{path}</c> — the pre-#587 URL. No longer produced
///   (that route served the mesh-level backing store with no permission check at all), but it is
///   persisted on every <c>CorporateIdentity</c> written before the fix, so dropping it would
///   silently blank those logos.</item>
/// </list>
///
/// <para>Anything else — an absolute URL, an unresolvable relative path — yields <c>null</c> and the
/// caller skips it.</para>
/// </summary>
public static class ContentPathReference
{
    /// <summary>The pre-#587 unauthenticated URL prefix. Recognised on read, never produced.</summary>
    public const string LegacyStaticPrefix = "/static/storage/content/";

    /// <summary>
    /// Maps a stored content reference to its <c>{collection}/{path}</c> form, or <c>null</c> when
    /// the value is not a content reference at all.
    /// </summary>
    /// <param name="path">The stored value (may be <c>null</c>).</param>
    /// <returns>The collection-relative reference, or <c>null</c>.</returns>
    public static string? TryGetRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (path.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
            return path["content:".Length..];

        if (path.StartsWith(LegacyStaticPrefix, StringComparison.OrdinalIgnoreCase))
            return path[LegacyStaticPrefix.Length..];

        var routePrefix = ContentCollectionsExtensions.ContentFileRoutePrefix + "/";
        if (!path.StartsWith(routePrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        // /api/content/{collection}/content/{path} — fold the default-collection segment out so the
        // result is the same {collection}/{path} the other two shapes produce.
        var rest = path[routePrefix.Length..];
        var marker = $"/{ContentCollectionsExtensions.DefaultCollectionName}/";
        var at = rest.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? rest : rest[..at] + "/" + rest[(at + marker.Length)..];
    }
}
