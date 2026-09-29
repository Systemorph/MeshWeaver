using System.Text;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// A <b>non-person principal</b> — a service, a bot, an integration — that authenticates with its
/// own <c>mw_</c> API tokens and is granted access like any other principal (an
/// <see cref="AccessAssignment"/> whose <see cref="AccessAssignment.AccessObject"/> is its
/// <see cref="ObjectIdPrefix">service object id</see>).
///
/// <para><b>Why it exists.</b> Every <c>mw_</c> token used to be issued only for the SIGNED-IN person:
/// a caller that is not a person (a name-check endpoint, a CI job, a partner integration) had to
/// borrow someone's personal token, so its writes were audited as that person and it carried that
/// person's whole reach. A service identity is the caller's own principal: its tokens authenticate as
/// the service, never as a person, and audit (<c>CreatedBy</c> / <c>LastModifiedBy</c>) shows the
/// service id.</para>
///
/// <para>🚨 <b>It lives in the Admin partition</b> (<see cref="Namespace"/>), exactly like
/// <see cref="BuildPrincipal"/> and for the same reason: only a global admin can write there, so only
/// a global admin creates a service identity, issues or revokes its tokens, or revokes the identity
/// itself — and the service can never write its own record. The token rows live beneath the record
/// (<see cref="TokenNamespace"/>), in the same partition.</para>
///
/// <para>🚨 <b>The object id is structurally distinct from every person's.</b> A service's
/// <c>AccessContext.ObjectId</c> is <see cref="ObjectIdPrefix"/> + its name (<c>svc-namecheck</c>).
/// Onboarding refuses a username carrying that prefix, and the request middleware refuses any
/// session that resolves to such an id WITHOUT having been authenticated by a service token — so an
/// e-mail whose local part happens to read <c>svc-…</c> can never step into a service's grants.</para>
///
/// <para>🚨 <b>A service principal never holds platform administration.</b>
/// <c>IsGlobalAdmin</c> answers <c>false</c> for a service object id whatever the grants say, an
/// <see cref="AccessAssignment"/> naming one in the Admin partition is refused, and a service
/// principal may not write anything in the Admin partition at all. See
/// <c>Doc/Architecture/ServiceIdentities</c>.</para>
/// </summary>
public record ServiceIdentity
{
    /// <summary>The node type of a service identity record.</summary>
    public const string NodeType = "ServiceIdentity";

    /// <summary>The namespace every service identity record lives in — under the Admin partition.</summary>
    public const string Namespace = "Admin/_ServiceIdentity";

    /// <summary>
    /// The prefix every service object id carries. No username can start with it (onboarding refuses
    /// it), so the prefix alone decides whether an object id names a service.
    /// </summary>
    public const string ObjectIdPrefix = "svc-";

    /// <summary>
    /// The claim the API-token authentication handler stamps on a service token's principal, with
    /// value <see cref="ServicePrincipalKind"/>. The request middleware honours it ONLY on an identity
    /// whose authentication type is <see cref="TokenAuthenticationType"/> — a cookie or an external
    /// provider cannot assert it.
    /// </summary>
    public const string PrincipalKindClaim = "mw_principal_kind";

    /// <summary>The <see cref="PrincipalKindClaim"/> value of a service principal.</summary>
    public const string ServicePrincipalKind = "Service";

    /// <summary>The authentication type (scheme name) of an identity built from an <c>mw_</c> API token.</summary>
    public const string TokenAuthenticationType = "ApiToken";

    /// <summary>What the service is and who owns it, in the issuing admin's words.</summary>
    public string Description { get; init; } = "";

    /// <summary>Object id of the global admin who created the identity.</summary>
    public string IssuedBy { get; init; } = "";

    /// <summary>When the identity was created.</summary>
    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>
    /// True once the identity is revoked. Every token of a revoked identity stops authenticating on
    /// the very next request: validation reads this record for every service token, so nothing has
    /// to walk the tokens first.
    /// </summary>
    public bool IsRevoked { get; init; }

    /// <summary>When the identity was revoked.</summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>Object id of the admin who revoked it.</summary>
    public string? RevokedBy { get; init; }

    /// <summary>True when <paramref name="objectId"/> names a service principal, not a person.</summary>
    /// <param name="objectId">An <c>AccessContext.ObjectId</c> or an <see cref="AccessAssignment.AccessObject"/>.</param>
    /// <returns><c>true</c> for a service object id.</returns>
    public static bool IsServiceObjectId(string? objectId) =>
        !string.IsNullOrEmpty(objectId)
        && objectId.StartsWith(ObjectIdPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The object id a service called <paramref name="name"/> gets: lowercase, every character
    /// outside <c>[a-z0-9-]</c> folded to <c>-</c>, prefixed with <see cref="ObjectIdPrefix"/> unless
    /// it already carries it. <c>null</c> when nothing usable is left, or when the result is longer
    /// than 64 characters.
    /// </summary>
    /// <param name="name">The name an admin typed — <c>NameCheck</c>, <c>svc-namecheck</c>, <c>CI bot</c>.</param>
    /// <returns>The object id, or <c>null</c>.</returns>
    public static string? ObjectIdFor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Trim().ToLowerInvariant())
            builder.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        var slug = builder.ToString().Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        if (slug.StartsWith(ObjectIdPrefix, StringComparison.Ordinal))
            slug = slug[ObjectIdPrefix.Length..];
        if (slug.Length == 0)
            return null;
        var objectId = ObjectIdPrefix + slug;
        return objectId.Length <= 64 ? objectId : null;
    }

    /// <summary>The record's path for a service object id.</summary>
    /// <param name="objectId">The service object id.</param>
    /// <returns><c>Admin/_ServiceIdentity/{objectId}</c>.</returns>
    public static string PathFor(string objectId) => $"{Namespace}/{objectId}";

    /// <summary>The namespace the service's token rows live in, beneath its record.</summary>
    /// <param name="objectId">The service object id.</param>
    /// <returns><c>Admin/_ServiceIdentity/{objectId}/ApiToken</c>.</returns>
    public static string TokenNamespace(string objectId) => $"{PathFor(objectId)}/ApiToken";

    /// <summary>
    /// Why a token that names <paramref name="objectId"/> and points at a record whose content is
    /// <paramref name="identity"/> may NOT authenticate — or <c>null</c> when it may. One predicate
    /// for both validation paths, so they cannot disagree.
    /// </summary>
    /// <param name="identity">The record read at the token's identity path, or <c>null</c> when absent.</param>
    /// <param name="objectId">The object id the token authenticates as.</param>
    /// <param name="identityPath">The identity path the token carries.</param>
    /// <returns>The refusal reason, or <c>null</c>.</returns>
    public static string? Refuse(ServiceIdentity? identity, string? objectId, string? identityPath)
    {
        if (objectId is null || !IsServiceObjectId(objectId))
            return $"the token carries an identity path but '{objectId}' is not a service object id";
        if (!string.Equals(identityPath, PathFor(objectId), StringComparison.OrdinalIgnoreCase))
            return $"the token's identity path '{identityPath}' is not the record of '{objectId}'";
        if (identity is null)
            return $"no service identity record at '{identityPath}'";
        if (identity.IsRevoked)
            return $"the service identity '{objectId}' is revoked";
        return null;
    }
}
