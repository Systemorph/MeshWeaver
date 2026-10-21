using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Well-known user IDs for access control.
/// </summary>
public static class WellKnownUsers
{
    /// <summary>
    /// The "Anonymous" user represents unauthenticated/virtual access.
    /// Assign roles to "Anonymous" to grant permissions to visitors who haven't logged in.
    /// </summary>
    public const string Anonymous = "Anonymous";

    /// <summary>
    /// The "Public" user represents the baseline permissions for all authenticated users.
    /// Every logged-in user inherits "Public" permissions in addition to their own.
    /// </summary>
    public const string Public = "Public";

    /// <summary>
    /// The "system-security" identity used by SecurityService for internal operations.
    /// Bypasses RLS validation — security operations (creating/updating AccessAssignment,
    /// PartitionAccessPolicy nodes) must not be blocked by the permissions they manage.
    /// </summary>
    public const string System = "system-security";

    /// <summary>
    /// The <see cref="AccessContext"/> of the <see cref="System"/> identity — the value to stamp on a
    /// post that must run as platform infrastructure.
    ///
    /// <para>🚨 Use this when the post cannot rely on the ambient AsyncLocal.
    /// <c>AccessService.ImpersonateAsSystem()</c> pushes the same identity onto AsyncLocal, but an
    /// AsyncLocal scope only covers the code that runs synchronously inside it — in a multi-stage Rx
    /// pipeline every stage after the first is subscribed from the previous stage's completion, on a
    /// pool/hub thread the scope never reached. Code that posts from there must carry the identity as
    /// a VALUE (<c>PostOptions.WithAccessContext</c>), not read it from the ambient.</para>
    /// </summary>
    public static AccessContext SystemContext { get; } = new() { ObjectId = System, Name = System };

    /// <summary>
    /// 🚨 THE ONE definition of "this caller is signed in" — the predicate every
    /// grant-to-all-authenticated-users rule MUST use.
    ///
    /// <para><b>Why it exists.</b> The obvious spelling — <c>!string.IsNullOrEmpty(userId)</c> —
    /// reads as "somebody is there" and is WRONG, because an unauthenticated caller is not
    /// nameless: it is named <see cref="Anonymous"/>, a perfectly non-empty string. Two access
    /// rules were spelled that way (<c>WithUserNodePublicRead</c>, <c>WithPublicRead</c>) and so
    /// handed a blanket Read grant to logged-OUT callers on every user partition hub. The route
    /// that serves a partition's uploaded files rides that decision, so a private partition's
    /// content was served to anonymous HTTP callers. The bitter irony of the old spelling: a
    /// caller with NO context at all was denied, and only naming the anonymous caller correctly
    /// opened the door.</para>
    ///
    /// <para><b>Fail closed.</b> Null, empty, whitespace and the well-known pseudo-subjects are
    /// all "not authenticated". <see cref="Anonymous"/> and <see cref="Public"/> exist only as
    /// grant SUBJECTS — neither is ever a real caller — so neither may satisfy a rule whose whole
    /// meaning is "a real user signed in".</para>
    ///
    /// <para><b>This does not deny anonymous callers by itself.</b> Failing this predicate only
    /// withholds the blanket grant; the request falls through to the real
    /// <c>PermissionEvaluator</c>, where an explicit Anonymous grant still allows the read. That
    /// is precisely the semantics the crawler-facing SEO route already relies on
    /// (<c>AnonymousGate</c>), so the two surfaces now agree.</para>
    /// </summary>
    /// <param name="userId">The caller identity, as resolved onto the delivery's AccessContext.</param>
    /// <returns><c>true</c> only for a real, signed-in user.</returns>
    public static bool IsAuthenticated(string? userId) =>
        !string.IsNullOrWhiteSpace(userId)
        && !string.Equals(userId, Anonymous, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(userId, Public, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the caller of a node-type access rule is a SIGNED-IN person: an authenticated id
    /// (<see cref="IsAuthenticated"/>) on a context that is not VIRTUAL. A logged-out visitor can
    /// carry any id — <see cref="Anonymous"/>, or a per-visitor one such as <c>guest-7f3a</c> — so
    /// the id alone never decides it; <see cref="AccessContext.IsVirtual"/> marks the visitor
    /// whatever it is called. Use it for every "any signed-in reader" shortcut in a type rule.
    /// </summary>
    /// <param name="context">The validated operation's access context, or null.</param>
    /// <param name="userId">The id the rule was handed.</param>
    /// <returns><c>true</c> only for a real, signed-in person.</returns>
    public static bool IsSignedIn(AccessContext? context, string? userId) =>
        IsAuthenticated(userId) && context?.IsVirtual != true;

    /// <summary>
    /// The subject a type rule's FALLBACK permission check evaluates when the caller is not
    /// signed in: <see cref="Anonymous"/> for a virtual (logged-out) context or a missing id, the id
    /// itself otherwise — so a visitor is judged by exactly the grants made to Anonymous.
    /// </summary>
    /// <param name="context">The validated operation's access context, or null.</param>
    /// <param name="userId">The id the rule was handed.</param>
    /// <returns>The subject to evaluate.</returns>
    public static string FallbackSubject(AccessContext? context, string? userId) =>
        context?.IsVirtual == true || string.IsNullOrEmpty(userId) ? Anonymous : userId;
}
