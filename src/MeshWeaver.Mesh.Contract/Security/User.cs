namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Content type for User nodes. Properties match the onboarding flow.
/// Avatar/Image is stored as MeshNode.Icon.
/// </summary>
public record User : AccessObject
{
    /// <summary>Full name from OAuth claims (e.g. "John Doe").</summary>
    public string? FullName { get; init; }

    /// <summary>Email address (from OAuth, set during onboarding).</summary>
    public string? Email { get; init; }

    /// <summary>Short biography.</summary>
    public string? Bio { get; init; }

    /// <summary>Profile role (e.g. Developer, Manager, Designer).</summary>
    public string? Role { get; init; }

    /// <summary>Ordered list of node paths the user has pinned to their dashboard.</summary>
    public IReadOnlyList<string> PinnedPaths { get; init; } = [];

    /// <summary>
    /// Long-form markdown body for the owner's home page — the SINGLE editable page, 1:1 with
    /// <c>Space.Body</c>. Empty/whitespace (the default) → the welcome template is shown, which embeds
    /// the home regions via <c>@@("area:…")</c> (Pinned, Catalog, Search, Composer). Set it to author
    /// the page yourself: keep, drop, reorder, or retune those embeds and add any markdown around them.
    /// Edits go to this one field (the assistant writes here too) — there is no per-segment override.
    /// </summary>
    public string? Body { get; init; }
}
