using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// 🛡️ A token's PRINCIPAL is bound to the caller that writes it. An API-token record's
/// <c>UserId</c> is what every token verdict authenticates as, so a write that could choose it would
/// let its writer authenticate as anyone, System included. The rule, for every writer except System:
/// <list type="bullet">
/// <item>a node that newly carries a token (a create, or a write that turns a node into one) may name
///   only the WRITER, with no service identity. A service token is the one exception: it may name a
///   service object id only from inside that service's own token namespace, with the matching identity
///   path, which only the platform admin surface can write;</item>
/// <item>a node that already carries a token keeps its principal and its service identity path: a
///   revoke or relabel lands, a rename never does.</item>
/// </list>
///
/// <para>The credential is recognised by its CONTENT, read exactly as the verdict reads it
/// (<see cref="ApiTokenVerdict.ReadToken"/>), not by the node type, so retyping a node cannot hide a
/// credential from the rule. It is enforced at both write seams: the create chain
/// (<see cref="RlsNodeValidator"/>) and the owner's commit point (<see cref="ApiTokenPrincipalCommitGuard"/>),
/// which every routed update, merge patch and data change meets.</para>
/// </summary>
public static class ApiTokenPrincipalBinding
{
    /// <summary>The refusal for writing <paramref name="after"/> in place of <paramref name="before"/>,
    /// or null when the write keeps the token principal bound.</summary>
    /// <param name="before">The node held before the write, or null for a create.</param>
    /// <param name="after">The node as written.</param>
    /// <param name="writerId">The writer's principal id.</param>
    /// <param name="locale">The writer's locale, for the refusal.</param>
    /// <param name="options">Serializer options that read the content.</param>
    /// <returns>The refusal, or null.</returns>
    public static string? Refusal(MeshNode? before, MeshNode after, string? writerId, string? locale,
        JsonSerializerOptions options)
    {
        if (string.Equals(writerId, WellKnownUsers.System, StringComparison.Ordinal))
            return null;
        if (ApiTokenVerdict.ReadToken(after, options) is not { TokenHash.Length: > 0 } token)
            return null;

        if (ApiTokenVerdict.ReadToken(before, options) is { TokenHash.Length: > 0 } previous)
            return string.Equals(previous.UserId, token.UserId, StringComparison.Ordinal)
                   && string.Equals(previous.ServiceIdentityPath ?? "", token.ServiceIdentityPath ?? "", StringComparison.Ordinal)
                ? null
                : LocalizationCatalog.Get("access.apiToken.principalFixed", locale, after.Path, previous.UserId);

        if (!string.IsNullOrEmpty(writerId)
            && string.Equals(token.UserId, writerId, StringComparison.Ordinal)
            && string.IsNullOrEmpty(token.ServiceIdentityPath))
            return null;
        if (ServiceIdentity.IsServiceObjectId(token.UserId)
            && string.Equals(token.ServiceIdentityPath, ServiceIdentity.PathFor(token.UserId!), StringComparison.Ordinal)
            && after.Path?.StartsWith(ServiceIdentity.TokenNamespace(token.UserId!) + "/", StringComparison.OrdinalIgnoreCase) == true)
            return null;
        return LocalizationCatalog.Get("access.apiToken.principalNotCaller", locale, after.Path, token.UserId, writerId);
    }
}

/// <summary>
/// <see cref="ApiTokenPrincipalBinding"/> at the owner's commit point, against the merged entity and
/// the one it replaces, for every route that commits into an owner.
/// </summary>
public sealed class ApiTokenPrincipalCommitGuard : IEntityCommitGuard
{
    /// <inheritdoc />
    public string? Refusal(string collection, object? before, object after, string ownerPath,
        AccessContext? writer, JsonSerializerOptions options)
    {
        // An unattributed commit is an internal one (activation saves, replays); a person's write
        // always reaches the owner with its context.
        if (writer is null
            || !string.Equals(collection, nameof(MeshNode), StringComparison.Ordinal)
            || after.As<MeshNode>(options) is not { } next)
            return null;
        return ApiTokenPrincipalBinding.Refusal(before?.As<MeshNode>(options), next, writer.ObjectId, writer.Locale, options);
    }
}
