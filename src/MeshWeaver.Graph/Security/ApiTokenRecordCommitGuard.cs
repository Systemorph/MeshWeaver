using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// 🛡️ The API-token scope cap's token-record rule at the owner's COMMIT point — the one invariant
/// every write that commits into an owner meets (review of #7585): a SCOPED writer commits a token
/// record ONLY as a create at the committing hub's own path. Every other commit that touches one is
/// refused:
/// <list type="bullet">
/// <item>a rewrite, retype into or out of <c>ApiToken</c> (a predecessor exists) — otherwise a token
///   could strip its own scopes, or recreate a deleted record as Markdown and retype it back unscoped;</item>
/// <item>a token record CARRIED to another node's hub (a <c>DataChangeRequest</c> whose entity is not
///   the receiving hub's own node) — there the workspace holds no predecessor, its own owner never sees
///   the write, and persistence would overwrite the credential.</item>
/// </list>
///
/// <para>The create it leaves is decided by the creation chain, where the RLS validator applies the
/// subset rule (<see cref="ApiTokenScope.MintRefusal"/>). Together with the verdict authenticating only
/// a node TYPED as a token record (<c>ApiTokenVerdict</c>), this closes the family: content alone never
/// authenticates, and no scoped write can produce a typed record except a subset-ruled mint.
/// Unscoped writers pass.</para>
/// </summary>
public sealed class ApiTokenRecordCommitGuard : IEntityCommitGuard
{
    /// <inheritdoc />
    public string? Refusal(string collection, object? before, object after, string ownerPath,
        AccessContext? writer, JsonSerializerOptions options)
    {
        if (!ApiTokenScope.IsScoped(writer)
            || !string.Equals(collection, nameof(MeshNode), StringComparison.Ordinal)
            || after.As<MeshNode>(options) is not { } next)
            return null;
        var previous = before?.As<MeshNode>(options);
        if (!ApiTokenScope.IsTokenRecordType(next.NodeType) && !ApiTokenScope.IsTokenRecordType(previous?.NodeType))
            return null;
        // The ONE exemption: a create at the committing hub's own path.
        if (previous is null && PolicyCommitGuard.IsOwnPath(next.Path, ownerPath))
            return null;
        return LocalizationCatalog.Get("access.apiTokenScope.mintRefused", writer!.Locale,
            ApiTokenScope.Format(writer.Scopes), next.Path,
            "a scoped token commits a token record only as a create at that record's own hub");
    }
}
