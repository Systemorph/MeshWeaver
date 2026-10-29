using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// 🛡️ The API-token scope cap's token-record rule at the owner's COMMIT point (review of #7585): a
/// scoped token never rewrites an existing node into, out of, or as an API-token record.
///
/// <para>The delivery gate (<c>AccessControlPipeline.CapScopedTokenRecordWrite</c>) sees only the node
/// the owner holds when the write arrives, so a write that turns an ordinary node INTO a token record
/// passes it: delete a minted token's record, recreate the path as Markdown, then retype it with
/// unscoped token content, and the surviving hash index authenticates the result unscoped. Here the
/// rule is judged against the merged entity AND the one it replaces, for every route that commits
/// into an owner (merge patch and data change).</para>
///
/// <para>A CREATE (nothing held under the key) is left to the creation chain, where the RLS validator
/// applies the subset rule (<see cref="ApiTokenScope.MintRefusal"/>). Unscoped writers pass.</para>
/// </summary>
public sealed class ApiTokenRecordCommitGuard : IEntityCommitGuard
{
    /// <inheritdoc />
    public string? Refusal(string collection, object? before, object after, string ownerPath,
        AccessContext? writer, JsonSerializerOptions options)
    {
        if (!ApiTokenScope.IsScoped(writer)
            || !string.Equals(collection, nameof(MeshNode), StringComparison.Ordinal)
            || before?.As<MeshNode>(options) is not { } previous
            || after.As<MeshNode>(options) is not { } next)
            return null;
        if (!ApiTokenScope.IsTokenRecordType(previous.NodeType) && !ApiTokenScope.IsTokenRecordType(next.NodeType))
            return null;
        return LocalizationCatalog.Get("access.apiTokenScope.mintRefused", writer!.Locale,
            ApiTokenScope.Format(writer.Scopes), next.Path,
            "a scoped token never rewrites a node into, out of, or as a token record");
    }
}
