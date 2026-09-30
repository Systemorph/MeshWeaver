namespace MeshWeaver.Messaging;

/// <summary>
/// Identity and authorization context of the caller on whose behalf a message is
/// processed. Carried on every <see cref="IMessageDelivery"/> and used by the
/// permission evaluator to authorize reads and writes.
/// </summary>
public record AccessContext
{
    /// <summary>
    /// Stable unique identifier of the principal (e.g. the Entra object id, or a
    /// hub's mesh address when <see cref="IsHub"/> is set). Empty when anonymous.
    /// </summary>
    public string ObjectId { get; init; } = string.Empty;
    /// <summary>
    /// Human-readable display name of the principal.
    /// </summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>
    /// Email address of the principal, when available.
    /// </summary>
    public string Email { get; init; } = string.Empty;
    /// <summary>
    /// The roles assigned to the principal, folded into the effective permissions.
    /// </summary>
    public IReadOnlyCollection<string> Roles { get; init; } = [];
    /// <summary>
    /// True when this context represents a virtual (non-interactive) user rather
    /// than a real signed-in person.
    /// </summary>
    public bool IsVirtual { get; init; }

    /// <summary>
    /// The principal's preferred display time zone as a named IANA zone id (e.g.
    /// <c>Europe/Zurich</c>, <c>America/New_York</c>), resolved from the user's profile
    /// when the context is built. Rides on the identity so every render path — Blazor
    /// circuit AND server-side hub layout areas that have no browser — can convert stored
    /// UTC timestamps to the viewer's local time via <c>AccessService.ToDisplayTime</c>.
    /// Null/empty → display in UTC. Never a fixed offset (DST would then be wrong).
    /// </summary>
    public string? TimeZoneId { get; init; }

    /// <summary>
    /// The principal's preferred UI language as a BCP-47 tag (e.g. <c>de</c>, <c>en</c>),
    /// resolved from the user's profile when the context is built. Rides on the identity —
    /// exactly like <see cref="TimeZoneId"/> — so every render path translates in the viewer's
    /// language: the Blazor circuit AND server-side hub layout areas that have no browser and
    /// no ambient <c>CultureInfo.CurrentUICulture</c>. Resolution is explicit (never ambient
    /// culture) because a hub render hops schedulers, where an AsyncLocal culture would not
    /// survive. Null/empty/unsupported → English.
    /// </summary>
    public string? Locale { get; init; }

    /// <summary>
    /// When set, indicates that this context is impersonated by another identity
    /// (e.g., a portal hub acting on behalf of a virtual user).
    /// The impersonator's identity is used for authorization when the
    /// impersonated user's own permissions are insufficient.
    /// </summary>
    public string? ImpersonatedBy { get; init; }

    /// <summary>
    /// When true, this context was established via an API token (Bearer authentication).
    /// The Api permission flag is required for operations in this context.
    /// </summary>
    public bool IsApiToken { get; init; }

    /// <summary>
    /// When true, this context is a SERVICE principal (a non-person identity, <c>ServiceIdentity</c>):
    /// <see cref="ObjectId"/> is a service object id (<c>svc-…</c>) and the context was established by
    /// one of that service's API tokens. Set ONLY by token validation — a session that resolves to a
    /// service object id without it is refused by the request middleware, and a service context never
    /// gets person-only treatment (onboarding, login tracking, logon actions). See
    /// Doc/Architecture/ServiceIdentities.
    /// </summary>
    public bool IsService { get; init; }

    /// <summary>
    /// When true, this context is a HUB credential: <see cref="ObjectId"/> is the hub's own
    /// mesh address (set by <c>ImpersonateAsHub</c>), not a user/group identity. A hub
    /// initializes and syncs its own EntityStore under this credential and a sub-hub subscribes
    /// to its parent/owner under it — so the permission evaluator grants a hub-credential
    /// <c>Read</c> on its OWN path and its ANCESTOR scopes (the sync direction), and nothing
    /// else. This is what lets a sub-hub read its parent hub without a per-hub-address
    /// <c>AccessAssignment</c> (which never exists). See AccessControl.md.
    /// </summary>
    public bool IsHub { get; init; }

    /// <summary>
    /// The path of the GOVERNED ACTIVITY this write executes (<c>Governance/Activities/{id}</c>), set
    /// only by the governance executor when it runs a signed standard as System. The broad-grant
    /// guard (<c>BroadGrantGuard</c>) lets a grant to Public/Anonymous, a grant written by System
    /// for somebody else, or a partition access-policy change through ONLY when this names an
    /// activity that is executing a standard on the allowlist. Null on every other context — a
    /// person's standing rights, however broad, never carry it. Posted as a value
    /// (<c>PostOptions.WithAccessContext</c>), never read from an ambient scope that does not
    /// survive a scheduler hop.
    /// </summary>
    public string? GovernedBy { get; init; }

    /// <summary>
    /// The ONE user a System-context write is made for — set by the Store's per-user enrollment
    /// (a subscription, a coupon, a purchase: the user acquiring access for themselves). A System
    /// grant whose subject equals this is that user's own acquisition and passes the broad-grant
    /// guard; a System grant for anyone else needs <see cref="GovernedBy"/>. Null means "for no
    /// one in particular", which is exactly the shape that granted a new free plugin to 72 users
    /// on 2026-09-29.
    /// </summary>
    public string? OnBehalfOf { get; init; }
}
