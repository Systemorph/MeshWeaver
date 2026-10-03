using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// The verbs on SERVICE identities (<see cref="ServiceIdentity"/>) — create, revoke, grant — shared
/// by the Admin app's Service identities tab and the tests, so both drive the SAME composition.
/// Token issuance, rotation and revocation are <see cref="ApiTokenService"/>'s
/// (<see cref="ApiTokenService.CreateServiceToken"/>, <see cref="ApiTokenService.RevokeToken"/>).
///
/// <para>🚨 Every write runs as the CALLER. The records live in the Admin partition, so only a
/// global admin can create or revoke one; a grant is an ordinary <see cref="AccessAssignment"/> at
/// the target scope, so it succeeds exactly where the caller may grant — a global admin is not a data
/// superuser, and this surface does not make them one. See <c>Doc/Architecture/ServiceIdentities</c>.</para>
/// </summary>
internal static class ServiceIdentities
{
    /// <summary>
    /// Creates the service identity called <paramref name="name"/>. Emits the created record.
    /// Refused (OnError) when the name yields no usable object id.
    /// </summary>
    /// <param name="hub">The hub to write through.</param>
    /// <param name="name">The display name — its object id is <see cref="ServiceIdentity.ObjectIdFor"/>.</param>
    /// <param name="description">What the service is and who owns it.</param>
    /// <returns>The created record node.</returns>
    public static IObservable<MeshNode> Create(IMessageHub hub, string name, string? description)
    {
        var objectId = ServiceIdentity.ObjectIdFor(name);
        if (objectId is null)
            return Observable.Throw<MeshNode>(new ArgumentException(
                $"'{name}' does not yield a service object id: use letters, digits and dashes, at most "
                + $"{64 - ServiceIdentity.ObjectIdPrefix.Length} characters."));

        var issuedBy = hub.ServiceProvider.GetRequiredService<AccessService>().Context?.ObjectId ?? "";
        var node = new MeshNode(objectId, ServiceIdentity.Namespace)
        {
            NodeType = ServiceIdentity.NodeType,
            Name = name.Trim(),
            State = MeshNodeState.Active,
            Content = new ServiceIdentity
            {
                Description = description?.Trim() ?? "",
                IssuedBy = issuedBy,
                IssuedAt = DateTimeOffset.UtcNow,
            },
        };
        return hub.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(node);
    }

    /// <summary>
    /// Revokes the service identity <paramref name="objectId"/>. Every token it holds stops
    /// authenticating on the next request — validation reads the record on every use — without
    /// touching the tokens themselves.
    /// </summary>
    /// <param name="hub">The hub to write through.</param>
    /// <param name="objectId">The service object id.</param>
    /// <returns>The updated record.</returns>
    public static IObservable<MeshNode> Revoke(IMessageHub hub, string objectId)
    {
        if (!ServiceIdentity.IsServiceObjectId(objectId))
            return Observable.Throw<MeshNode>(new ArgumentException($"'{objectId}' is not a service object id."));
        var revokedBy = hub.ServiceProvider.GetRequiredService<AccessService>().Context?.ObjectId;
        var path = ServiceIdentity.PathFor(objectId);
        // Existence first, from the authoritative store (a point read of an absent node is a routing
        // NotFound, not an answer): a typo'd or deleted id is refused by name, never "revoked".
        return hub.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, hub.JsonSerializerOptions)
            .Take(1)
            .DefaultIfEmpty()
            .SelectMany(record => record?.ContentAs<ServiceIdentity>(hub.JsonSerializerOptions) is null
                ? Observable.Throw<MeshNode>(new ArgumentException(
                    $"No service identity '{objectId}' exists at {path}."))
                : RevokeExisting(hub, path, revokedBy));
    }

    private static IObservable<MeshNode> RevokeExisting(IMessageHub hub, string path, string? revokedBy)
        => hub.GetWorkspace()
            .GetMeshNodeStream(path)
            .Update(node =>
            {
                var identity = node.ContentAs<ServiceIdentity>(hub.JsonSerializerOptions);
                if (identity is null || identity.IsRevoked)
                    return node;
                return node with
                {
                    Content = identity with
                    {
                        IsRevoked = true,
                        RevokedAt = DateTimeOffset.UtcNow,
                        RevokedBy = revokedBy,
                    },
                };
            });

    /// <summary>
    /// Sets the service <paramref name="objectId"/>'s role at <paramref name="scopePath"/> to
    /// <paramref name="role"/> — the <c>{scope}/_Access/{objectId}_Access</c>
    /// <see cref="AccessAssignment"/> every principal's grant is (create-or-update, idempotent).
    /// 🚨 It SETS, it does not add: a service holds one role per scope through this verb, so granting
    /// <c>Viewer</c> where it held <c>Editor</c> replaces the <c>Editor</c> grant. That is the intent
    /// for a machine principal (least privilege, one answer per scope); compose several roles on one
    /// subject through the node's Access Control tab instead.
    /// Refused for a non-service id, and — by <c>ServicePrincipalAdminGuard</c> — anywhere in the
    /// Admin partition.
    /// </summary>
    /// <param name="hub">The hub to write through.</param>
    /// <param name="objectId">The service object id.</param>
    /// <param name="scopePath">The node path the grant applies at (and below).</param>
    /// <param name="role">The role id — <c>Viewer</c>, <c>Editor</c>, …</param>
    /// <returns>The assignment node.</returns>
    public static IObservable<MeshNode> Grant(IMessageHub hub, string objectId, string scopePath, string role)
    {
        if (!ServiceIdentity.IsServiceObjectId(objectId))
            return Observable.Throw<MeshNode>(new ArgumentException($"'{objectId}' is not a service object id."));
        var scope = scopePath.Trim().Trim('/');
        if (scope.Length == 0)
            return Observable.Throw<MeshNode>(new ArgumentException(
                "A service is granted at a named scope, never at the mesh root."));
        if (string.IsNullOrWhiteSpace(role))
            return Observable.Throw<MeshNode>(new ArgumentException("A grant names a role."));
        var node = new MeshNode($"{objectId}_Access", $"{scope}/_Access")
        {
            NodeType = AccessAssignmentNodeType.NodeType,
            Name = $"{objectId} Access",
            MainNode = scope,
            Content = new AccessAssignment
            {
                AccessObject = objectId,
                DisplayName = objectId,
                Roles = [new RoleAssignment { Role = role.Trim(), Denied = false }],
            },
        };
        return hub.ServiceProvider.GetRequiredService<IMeshService>().CreateOrUpdateNode(node);
    }

    /// <summary>
    /// Rotates a service token: issues a fresh token with the same label and end of term, THEN
    /// revokes the old one — mint-then-revoke, so a failed mint leaves the service its working
    /// credential. Emits the new token (shown once).
    /// </summary>
    /// <param name="tokens">The token service.</param>
    /// <param name="objectId">The service object id.</param>
    /// <param name="oldTokenPath">The path of the token being replaced.</param>
    /// <param name="label">The label to carry over.</param>
    /// <param name="expiresAt">The end of term to carry over.</param>
    /// <returns>The new token.</returns>
    public static IObservable<TokenCreationResult> Rotate(
        ApiTokenService tokens, string objectId, string oldTokenPath, string label, DateTimeOffset? expiresAt)
        => tokens.CreateServiceToken(objectId, label, expiresAt)
            .SelectMany(created => tokens.RevokeToken(oldTokenPath).Select(_ => created));
}
