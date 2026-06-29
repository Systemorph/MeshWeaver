namespace MeshWeaver.Messaging;

/// <summary>
/// Declares, as part of a hub's configuration, UNDER WHICH IDENTITY the hub posts
/// — the never-null AccessContext invariant made explicit at hub startup
/// (<c>feedback_access_context_always_set</c>). Every hub posts under exactly one
/// of these; there is no fourth source and no silent null.
///
/// <para><b>AccessContext must ALWAYS be set.</b> This makes the source unambiguous
/// per hub rather than relying on every callsite getting impersonation right:</para>
/// </summary>
public enum PostingIdentity
{
    /// <summary>
    /// DEFAULT. The hub posts under the USER identity — it expects the user's
    /// <see cref="AccessContext"/> to be live on the AsyncLocal
    /// (<see cref="AccessService.Context"/> / <see cref="AccessService.CircuitContext"/>)
    /// when it posts. This is the shape for user-facing hubs (the per-circuit portal
    /// hub, HTTP-request-scoped hubs, per-node hubs handling a user's request).
    ///
    /// <para>🚨 When the AsyncLocal is NOT set and the message is not exempt
    /// (<c>[SystemMessage]</c> / <c>[CanBeIgnored]</c> / <c>DeliveryFailure</c>),
    /// the post is UNHAPPY: the PostPipeline logs an error and FAILS the delivery
    /// immediately (no identity, no delivery). The post lost the user identity —
    /// surface it and wire its source rather than silently failing closed.</para>
    /// </summary>
    User = 0,

    /// <summary>
    /// The hub is FRAMEWORK INFRASTRUCTURE — routing (the courier) and persistence
    /// (the store). Its posts run as <c>System</c> automatically: when a post has no
    /// pre-set <see cref="AccessContext"/>, the PostPipeline stamps the well-known
    /// <c>system-security</c> identity (which the security layer grants
    /// <c>Permission.All</c>). No per-callsite <c>ImpersonateAsSystem</c> needed.
    ///
    /// <para>This NEVER overwrites a user identity that is already on the delivery
    /// (e.g. a forwarded user delivery, or a response inheriting the request's
    /// identity via <see cref="PostOptions.ResponseFor"/>) — System is only the
    /// fallback for the hub's OWN otherwise-unattributed posts.</para>
    /// </summary>
    System = 1,
}
