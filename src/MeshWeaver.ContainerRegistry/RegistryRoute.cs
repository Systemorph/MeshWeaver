namespace MeshWeaver.ContainerRegistry;

/// <summary>
/// One parsed OCI pull route. The spec allows a repository NAME to contain slashes
/// (<c>team/service</c>), so the kind and reference are taken from the END of the path, never by
/// splitting from the front — <c>a/b/c/manifests/latest</c> is repository <c>a/b/c</c>.
/// </summary>
/// <param name="Repository">The repository name, slashes intact.</param>
/// <param name="Kind">One of <c>manifests</c>, <c>blobs</c>, or <c>tags</c>.</param>
/// <param name="Reference">A tag, a digest, or <c>list</c> for the tags route.</param>
public readonly record struct RegistryRoute(string Repository, string Kind, string Reference)
{
    /// <summary>Parses the catch-all remainder after <c>/v2/</c>. Returns false for anything that
    /// is not a pull route — including uploads and deletes, which this mirror does not serve.</summary>
    public static bool TryParse(string rest, out RegistryRoute route)
    {
        route = default;
        if (string.IsNullOrWhiteSpace(rest))
            return false;

        var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // repository (>=1) + kind + reference
        if (parts.Length < 3)
            return false;

        var kind = parts[^2];
        var reference = parts[^1];
        var repository = string.Join('/', parts[..^2]);

        if (repository.Length == 0)
            return false;

        // 🚨 Path traversal: a segment of ".." would let a caller walk out of the repository the
        // allowlist checked. Refuse rather than normalise — normalising invites a second bug.
        if (parts.Any(p => p is "." or ".."))
            return false;

        var ok = kind switch
        {
            // A manifest reference is a tag or a digest — both are opaque here, and the upstream
            // is the authority on whether it exists.
            "manifests" => true,
            // 🚨 A blob is ALWAYS addressed by digest (OCI Distribution §3). Accepting anything
            // else lets `blobs/uploads/` — the PUSH route — through as a pull: the parse succeeds
            // with reference "uploads" once the trailing empty segment is dropped, and the mirror
            // would forward an upload path it does not serve. Requiring the digest shape refuses
            // the whole upload family by construction rather than by blocklisting names.
            "blobs" => IsDigest(reference),
            "tags" => reference == "list",
            _ => false,
        };
        if (!ok)
            return false;

        route = new RegistryRoute(repository, kind, reference);
        return true;
    }

    /// <summary>A content digest: <c>algorithm:hex</c>, e.g. <c>sha256:ab12…</c>.</summary>
    private static bool IsDigest(string reference)
    {
        var colon = reference.IndexOf(':');
        if (colon <= 0 || colon == reference.Length - 1)
            return false;
        for (var i = 0; i < colon; i++)
            if (!char.IsAsciiLetterOrDigit(reference[i]))
                return false;
        for (var i = colon + 1; i < reference.Length; i++)
            if (!char.IsAsciiHexDigitLower(reference[i]))
                return false;
        return true;
    }
}
