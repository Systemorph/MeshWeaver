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

    /// <summary>Short biography. Rendered read-only as markdown on the public profile; edited via a
    /// node-bound markdown editor (see <c>UserActivityLayoutAreas.EditProfile</c>).</summary>
    public string? Bio { get; init; }

    /// <summary>
    /// Public profile links as a markdown block — one link per line, e.g.
    /// <c>[GitHub](https://github.com/you)</c>. Rendered read-only via <c>Controls.Markdown</c> (so the
    /// framework renders the anchors — no hand-rolled HTML) and edited via a node-bound markdown editor.
    /// This is opt-in public content: only what the owner writes here is shown to visitors.
    /// </summary>
    public string? Links { get; init; }

    /// <summary>Profile role (e.g. Developer, Manager, Designer).</summary>
    public string? Role { get; init; }

    /// <summary>
    /// The user's preferred display time zone as a named IANA zone id (e.g.
    /// <c>Europe/Zurich</c>, <c>America/New_York</c>). Timestamp STORAGE stays UTC —
    /// this drives the per-viewer DISPLAY conversion only (see
    /// <c>AccessService.ToDisplayTime</c>). Named zones (never fixed offsets) so DST is
    /// applied automatically and per-region. Empty/null → the viewer sees UTC. Populated
    /// once from the browser on first sign-in and overridable in user settings.
    /// </summary>
    public string? TimeZoneId { get; init; }

    /// <summary>
    /// The user's preferred UI language as a BCP-47 tag (e.g. <c>en</c>, <c>de</c>). Drives the
    /// per-viewer TEXT seam (<c>AccessService.Localize</c>), resolved once onto
    /// <c>AccessContext.Locale</c> when the circuit context is built — exactly as
    /// <see cref="TimeZoneId"/> drives the timestamp seam. Content STORAGE is unaffected: only
    /// UI chrome is translated. A region tag falls back to its primary subtag
    /// (<c>de-CH</c> → <c>de</c>) and an unsupported/empty value falls back to English, so the
    /// UI is never blank — just untranslated. Populated once from the browser's
    /// <c>navigator.language</c> on first sign-in and overridable in user settings.
    /// </summary>
    public string? Locale { get; init; }

    /// <summary>Ordered list of node paths the user has pinned to their dashboard.</summary>
    public IReadOnlyList<string> PinnedPaths { get; init; } = [];

    /// <summary>
    /// The node paths this user marked "hide in presentation mode" (issue #1803) — their PERSONAL
    /// privacy screen, stored here beside <see cref="PinnedPaths"/> for exactly the same reason:
    /// it is a preference of the VIEWER, never a property of the shared node. Disguising a node's
    /// display fields instead is global — every user sees the renamed tile and it needs a restore
    /// step — which is the workaround this replaces.
    ///
    /// <para>🚨 DISPLAY-ONLY, and only while <see cref="PresentationMode"/> is on. A marked path is
    /// still readable, still resolves by direct URL, still comes back from the mesh query engine,
    /// and is completely unchanged for every other user: the marking grants nothing and denies
    /// nothing. See <see cref="PresentationScreen"/>, which is the one place the two fields are
    /// turned into a rendering decision.</para>
    ///
    /// <para>Marking a space covers its subtree — a path IS a name, so listing
    /// <c>Acme/Q3-Renewal</c> under "Last edited" would leak exactly what marking <c>Acme</c> was
    /// meant to keep off the screen.</para>
    /// </summary>
    public IReadOnlyList<string> HiddenPaths { get; init; } = [];

    /// <summary>
    /// Whether this user's presentation mode is currently ON — the quick toggle flipped before
    /// sharing a screen (issue #1803). While it is on, every path in <see cref="HiddenPaths"/>
    /// disappears from the user's OWN tile, card and completion surfaces; while it is off, the
    /// markings do nothing at all, so turning it off is the complete undo with no restore step.
    ///
    /// <para>It lives on the profile rather than in circuit state so the mode survives navigation
    /// and a reconnect mid-presentation — a privacy screen that silently drops on a dropped
    /// WebSocket is worse than none. Signing out and back in keeps it on, deliberately and
    /// visibly: the header indicator is lit whenever it is.</para>
    /// </summary>
    public bool PresentationMode { get; init; }

    /// <summary>
    /// Long-form markdown body for the owner's home page — the SINGLE editable page, 1:1 with
    /// <c>Space.Body</c>. Empty/whitespace (the default) → the welcome template is shown, which embeds
    /// the home regions via <c>@@("area/…")</c> (Pinned, Catalog, Search, Composer). Set it to author
    /// the page yourself: keep, drop, reorder, or retune those embeds and add any markdown around them.
    /// Edits go to this one field (the assistant writes here too) — there is no per-segment override.
    /// </summary>
    public string? Body { get; init; }
}
