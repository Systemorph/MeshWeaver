using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Canonical synced-query lookups for <c>ApiToken</c> nodes. Validation finds a token by its hash via
/// <c>workspace.GetQuery</c> (a <c>content.tokenHash</c> field match) — the same shape as
/// <c>OnboardingMiddleware.FindUserByEmail</c> resolving a user by email. No per-hash index, no cache:
/// the token hash is unique, the query bypasses RLS and stays live (so a revoked token is rejected at
/// once), and it resolves through the <c>auth</c> mirror schema in prod / cross-partition fan-out in tests.
/// </summary>
public static class ApiTokenQueries
{
    /// <summary>
    /// Live lookup of the <c>ApiToken</c> node whose <c>content.tokenHash</c> equals
    /// <paramref name="tokenHash"/>; emits the node (or <c>null</c>) on the first synced-query snapshot.
    /// </summary>
    public static IObservable<MeshNode?> GetApiTokenByHash(this IWorkspace workspace, string tokenHash)
        => workspace.GetQuery(
                $"auth:tokenByHash:{tokenHash}",
                $"nodeType:{ApiTokenNodeType.NodeType} content.tokenHash:{tokenHash} limit:1")
            .Take(1)
            .Select(items => items.FirstOrDefault());
}
