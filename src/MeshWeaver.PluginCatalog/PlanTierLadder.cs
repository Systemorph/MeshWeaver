using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Reads the subscription-plan ladder a plan-scoped <see cref="PluginGrantEntry"/> is decided
/// against — the <c>Admin/Tiers/{id}</c> nodes the Store seeds, one per plan, carrying
/// <c>content.rank</c> (cheap → capable) and <c>content.allAccess</c> — into a
/// <see cref="PlanTierRanks"/> snapshot.
///
/// <para>The ladder is the STORE's data, deliberately not a table in the platform: the Store owns
/// the plans, prices and ranks, an operator edits them as nodes, and this read is the one place the
/// registry learns them — so there is no copy of <c>PlanTiers</c> here to drift. The node's
/// content type (<c>TierContent</c>) is Store in-mesh source the platform never references; the
/// two fields are read shape-tolerantly off the serialized content, exactly as the authenticator
/// reads its records.</para>
///
/// <para>Listing the children of one namespace is a valid query use (CQRS: a stale plan rank is
/// harmless for a minute, and there is no single node to point-read). The result is cached per
/// mesh for <see cref="CacheDuration"/> — the same window the authenticator reuses a verdict for —
/// as an instance field, never a static. A read that fails yields the LAST snapshot when there is
/// one and <see cref="PlanTierRanks.Empty"/> otherwise, so a plan-scoped entry fails CLOSED
/// while plan-less entries are unaffected; the failure is logged, never surfaced as a denial's
/// reason to the caller.</para>
/// </summary>
public sealed class PlanTierLadder(IMessageHub hub, ILogger<PlanTierLadder> logger)
{
    /// <summary>Where the Store seeds one node per plan.</summary>
    public const string Namespace = "Admin/Tiers";

    /// <summary>How long a snapshot is reused before the tier nodes are listed again.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    // Instance state on a mesh-scoped singleton — dies with the mesh (NoStaticState).
    private (DateTimeOffset At, PlanTierRanks Ranks)? cached;

    /// <summary>The ladder — cached, or freshly listed from the tier nodes. Cold; emits once.</summary>
    public IObservable<PlanTierRanks> Read()
    {
        if (cached is { } hit && DateTimeOffset.UtcNow - hit.At < CacheDuration)
            return Observable.Return(hit.Ranks);

        return hub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{Namespace}"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Take(1)
            .Timeout(ReadTimeout)
            .Select(c => PlanTierRanks.From(c.Items.Select(Plan)))
            .Do(ranks =>
            {
                cached = (DateTimeOffset.UtcNow, ranks);
                if (ranks.Ranks.Count == 0)
                    logger.LogInformation(
                        "Plan ladder: no tier nodes under {Namespace} — plan-scoped grant entries license nothing here",
                        Namespace);
            })
            .Catch((Exception ex) =>
            {
                var fallback = cached?.Ranks ?? PlanTierRanks.Empty;
                logger.LogWarning(ex,
                    "Plan ladder: listing {Namespace} failed — deciding with {What}",
                    Namespace, cached is null ? "an EMPTY ladder (plan-scoped entries fail closed)" : "the last snapshot");
                return Observable.Return(fallback);
            });
    }

    /// <summary>One tier node → <c>(id, rank, allAccess)</c>. The id is the node's, the two fields
    /// are read off the content whatever CLR shape it arrived in (typed Store content on a mesh
    /// that runs the Store, a <see cref="JsonElement"/> anywhere else); a missing rank reads 0.</summary>
    private (string Id, int Rank, bool AllAccess) Plan(MeshNode node)
    {
        var rank = 0;
        var allAccess = false;
        try
        {
            var content = node.Content is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(node.Content, hub.JsonSerializerOptions);
            if (content.ValueKind == JsonValueKind.Object)
            {
                if (Property(content, "rank") is { ValueKind: JsonValueKind.Number } r && r.TryGetInt32(out var parsed))
                    rank = parsed;
                allAccess = Property(content, "allAccess") is { ValueKind: JsonValueKind.True };
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            logger.LogWarning(ex, "Plan ladder: could not read tier node {Path}", node.Path);
        }
        return (node.Id ?? "", rank, allAccess);
    }

    /// <summary>Case-insensitive property lookup — the content is camelCase on the wire, PascalCase
    /// when a typed record was serialized with other options.</summary>
    private static JsonElement? Property(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value;
        return null;
    }
}
