namespace MeshWeaver.Maui.Abstractions;

/// <summary>
/// Pure helpers for turning a <c>NavLinkControl.Url</c> (a mesh path in the portal URL shape
/// <c>{meshpath}</c>) into the node path the native shell navigates to. The mesh URL shape is
/// <c>{baseUrl}/{meshpath}</c>; a leading <c>/</c> and the local-only <c>@/</c> (or bare <c>@</c>)
/// prefix are tolerated and stripped.
/// </summary>
public static class MauiHref
{
    /// <summary>Strips a leading <c>@/</c> / <c>@</c> prefix and leading slashes, yielding the node path.</summary>
    public static string Normalize(string href)
    {
        var s = (href ?? "").Trim();
        if (s.StartsWith("@/", StringComparison.Ordinal)) s = s[2..];
        else if (s.StartsWith('@')) s = s[1..];
        return s.TrimStart('/');
    }

    /// <summary>The last path segment of <paramref name="path"/> (its display title), or the whole path.</summary>
    public static string LastSegment(string path)
    {
        if (string.IsNullOrEmpty(path)) return path ?? "";
        var i = path.LastIndexOf('/');
        return i >= 0 && i < path.Length - 1 ? path[(i + 1)..] : path;
    }
}
