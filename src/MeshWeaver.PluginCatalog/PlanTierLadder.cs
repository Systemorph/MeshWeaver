using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
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
/// <para>🚨 <b>A LIVE listing, never a query per read (MeshWeaver#7354).</b> The ladder is read on
/// every instance-key resolution — every registry request an installation makes. It used to be an
/// <c>IMeshService.Query</c> re-run whenever a one-minute snapshot had expired, uncoalesced, on the
/// request path and outside the resolution's deadline: a consumer polling less often than once a
/// minute paid a full mesh query (bounded only by the query fan-in's own budget) on EVERY request,
/// stacked after the ten-second key legs and inside the registry's twenty-five-second answer
/// deadline. It is now ONE synced listing of <see cref="Namespace"/> in the process-wide
/// <c>IMeshNodeStreamCache</c> (<see cref="ListingId"/>), hydrated once per process and kept current
/// by the change feed: a read takes the listing's current snapshot from memory, and a tier added,
/// re-ranked or removed is seen by the next read rather than after a window. The listing runs as
/// System — the ladder is decision input the registry needs whoever is asking, and one shared
/// System chain is what keeps it ONE listing rather than one per caller.</para>
///
/// <para>A listing that FAULTS yields the LAST folded ladder when there is one and
/// <see cref="PlanTierRanks.Empty"/> otherwise, so a plan-scoped entry fails CLOSED while plan-less
/// entries are unaffected; the failure is logged, never surfaced as a denial's reason to the
/// caller. The cache evicts a faulted listing, so the next read opens a fresh one — nothing here
/// retries on its own.</para>
/// </summary>
public sealed class PlanTierLadder(IMessageHub hub, ILogger<PlanTierLadder> logger)
{
    /// <summary>Where the Store seeds one node per plan.</summary>
    public const string Namespace = "Admin/Tiers";

    /// <summary>The id of the ONE synced listing of <see cref="Namespace"/> in the process-wide
    /// cache. Stable, so every read shares it.</summary>
    internal const string ListingId = "plan-tier-ladder";

    /// <summary>The listing's query: the tier nodes, content exactly as stored — the Store's
    /// <c>TierContent</c> is in-mesh source the platform never references, and the fold reads the
    /// two fields shape-tolerantly anyway.</summary>
    internal const string ListingQuery = $"path:{Namespace} scope:children {ParsedQuery.StoredContentQualifier}";

    /// <summary>Retained for source compatibility with callers compiled outside this repository.
    /// Since MeshWeaver#7354 there is no snapshot window: the ladder is a live listing.</summary>
    [Obsolete("The plan ladder is a live listing since MeshWeaver#7354; there is no cache window.")]
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    /// <summary>One folded listing snapshot — the listing's emission it was folded from (by
    /// REFERENCE: the listing replays the same snapshot to every reader until it changes) and the
    /// ladder it yielded.</summary>
    private sealed record Folded(IEnumerable<MeshNode> Nodes, PlanTierRanks Ranks);

    // Instance state on a mesh-scoped singleton — dies with the mesh (NoStaticState). Written and
    // read as one reference.
    private Folded? folded;

    /// <summary>The ladder — the live listing's current snapshot, folded. Cold; emits once.</summary>
    public IObservable<PlanTierRanks> Read() =>
        hub.ServiceProvider.GetService<AccessService>()
            .RunAsSystem(() => hub.GetQuery(ListingId, ListingQuery))
            .Take(1)
            // No bound of its own: the listing's first hydration is bounded by the query fan-in's
            // stall terminal (MeshOperationOptions.QueryInitialBudget), which faults into the Catch
            // below. A shorter local timer here would give up while the listing is still inside its
            // own budget — the inverted ladder of #1198 (#5894). After that first hydration every
            // read answers from memory.
            .Select(Fold)
            .Catch((Exception ex) =>
            {
                var last = Volatile.Read(ref folded);
                logger.LogWarning(ex,
                    "Plan ladder: listing {Namespace} failed — deciding with {What}",
                    Namespace, last is null ? "an EMPTY ladder (plan-scoped entries fail closed)" : "the last ladder");
                return Observable.Return(last?.Ranks ?? PlanTierRanks.Empty);
            });

    /// <summary>The ladder for one listing snapshot — folded (and its findings logged) once per
    /// snapshot, then served from the held fold until the listing changes.</summary>
    private PlanTierRanks Fold(IEnumerable<MeshNode> nodes)
    {
        if (Volatile.Read(ref folded) is { } hit && ReferenceEquals(hit.Nodes, nodes))
            return hit.Ranks;

        var items = nodes.ToArray();
        foreach (var retired in items.Where(n => PlanTierRanks.SuccessorOf(n.Id) is not null))
            logger.LogWarning(
                "Plan ladder: tier node {Path} carries the RETIRED plan id '{Retired}' — ignored; "
                + "'{Retired}' resolves to '{Successor}', whose own tier node ranks it",
                retired.Path, retired.Id, retired.Id, PlanTierRanks.SuccessorOf(retired.Id));
        var ranks = PlanTierRanks.From(items.Select(Plan));
        if (ranks.Ranks.Count == 0)
            logger.LogInformation(
                "Plan ladder: no tier nodes under {Namespace} — plan-scoped grant entries license nothing here",
                Namespace);
        Volatile.Write(ref folded, new Folded(nodes, ranks));
        return ranks;
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
