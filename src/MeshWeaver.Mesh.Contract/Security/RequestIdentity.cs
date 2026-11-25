using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// 🚨 <b>Who a request is authorised as, and recorded under — never the caller's choice.</b>
///
/// <para>Every client ingress (SignalR, gRPC, the HTTP middleware) stamps the DELIVERY with the
/// authenticated caller. Node-operation messages also carry identity FIELDS:
/// <c>CreateNodeRequest.CreatedBy</c>, <c>CreateNodesRequest.CreatedBy</c>,
/// <c>DeleteNodeRequest.DeletedBy</c>, <c>CreateOrUpdateNodeRequest.RequestedBy</c>, and the
/// node's own <c>CreatedBy</c>/<c>LastModifiedBy</c>. Those fields exist for the platform's own
/// writers. Their AsyncLocal context does not survive every hop, so a System import posts its
/// identity as a value. They were also read FIRST, so a client that typed
/// <c>createdBy: "system-security"</c> into a message body was authorised as System
/// (measured: an identity with no grant created a node in another user's space).</para>
///
/// <para><b>The rule.</b> When the delivery carries an authenticated principal that is not the
/// platform itself, that principal is the requester, whatever the fields say. A field may name a
/// different identity only when the platform posted the message: no context, the System
/// identity, or a hub-shaped principal. No client can reach that branch, because every ingress
/// stamps the delivery.</para>
/// </summary>
public static class RequestIdentity
{
    /// <summary>
    /// The platform itself: the System identity or a hub-shaped principal (<c>mesh/…</c>,
    /// <c>node/…</c>, …). Only these may post a message whose identity field names somebody else.
    /// Pure.
    /// </summary>
    public static bool IsPlatform(string? objectId) => AccessService.IsPlatformPrincipal(objectId);

    /// <summary>
    /// An id no sign-in may adopt: the System identity, the two audience pseudo-users
    /// (<see cref="WellKnownUsers.Anonymous"/>, <see cref="WellKnownUsers.Public"/>) and any
    /// hub-shaped principal. A session's id is derived from claims an external provider issues
    /// (the local part of <c>preferred_username</c> or the email). The portal accepts multi-tenant
    /// sign-in, so an account named <c>system-security@some-tenant</c> would otherwise BE the
    /// System identity, which holds every permission. Pure.
    /// </summary>
    public static bool IsReservedPrincipal(string? objectId) =>
        IsPlatform(objectId)
        || string.Equals(objectId, WellKnownUsers.Anonymous, StringComparison.OrdinalIgnoreCase)
        || string.Equals(objectId, WellKnownUsers.Public, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The identity a request is authorised and recorded as. It is the delivery's principal when
    /// that principal is authenticated and not the platform. Otherwise it is
    /// <paramref name="requestField"/> when that is set, else the delivery's principal (which may
    /// be null). Pure.
    /// </summary>
    /// <param name="requestField">The identity the message body names (<c>CreatedBy</c>, <c>DeletedBy</c>, …).</param>
    /// <param name="delivery">The context the ingress stamped on the delivery.</param>
    public static string? Resolve(string? requestField, AccessContext? delivery)
    {
        var who = delivery?.ObjectId;
        if (!string.IsNullOrEmpty(who) && !IsPlatform(who))
            return who;
        return string.IsNullOrEmpty(requestField) ? who : requestField;
    }

    /// <summary>
    /// The author stamp a node is written with: <paramref name="identity"/> when a person or
    /// service is writing, because nobody records somebody else as the author. Otherwise, when
    /// the platform writes (an import or a repair that preserves who wrote the content), it is the
    /// node's own stamp when set, else <paramref name="identity"/>. Pure.
    /// </summary>
    /// <param name="nodeStamp">The <c>CreatedBy</c>/<c>LastModifiedBy</c> the incoming node carries.</param>
    /// <param name="identity">The resolved requester (<see cref="Resolve"/>).</param>
    public static string? Author(string? nodeStamp, string? identity) =>
        !string.IsNullOrEmpty(identity) && !IsPlatform(identity)
            ? identity
            : string.IsNullOrEmpty(nodeStamp) ? identity : nodeStamp;

    /// <summary>
    /// The creation/modification INSTANT a node is written with: the server clock
    /// (<paramref name="now"/>) whenever a person or service is writing, because nobody backdates a
    /// record — the stamp is the moment the owner accepted the write, and an append-only consumer
    /// (newest save wins, an audit trail) reads it as exactly that. Only the platform (a GitSync
    /// import, a repair) preserves a carried stamp, so history survives. An unresolved (empty)
    /// identity is NOT the platform and is stamped like a person — unlike <see cref="Author"/>, which
    /// falls back to the node's own stamp there. A carried default gets <paramref name="now"/>. Pure. Companion of <see cref="Author"/>.
    /// </summary>
    /// <param name="nodeStamp">The <c>CreatedDate</c>/<c>LastModified</c> the incoming node carries.</param>
    /// <param name="identity">The resolved requester (<see cref="Resolve"/>).</param>
    /// <param name="now">The owner's own storage-stable clock reading.</param>
    public static DateTimeOffset Moment(DateTimeOffset nodeStamp, string? identity, DateTimeOffset now) =>
        nodeStamp != default && !string.IsNullOrEmpty(identity) && IsPlatform(identity)
            ? MeshNode.StorageStable(nodeStamp)
            : now;
}
