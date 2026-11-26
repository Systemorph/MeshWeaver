using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// One replica's claim on ONE firing of a timer <see cref="EventSubscription"/> — the cross-replica
/// single-flight. Every replica runs its own <see cref="EventSubscriptionRunner"/> with its own in-memory
/// timer over the same durable subscription, so at the slot all of them fire. The claim is a node whose id
/// is the slot (<see cref="EventFiringClaimNodeType.IdOf"/>); the create is atomic in the store, so exactly
/// one replica wins it and runs the continuation, and the others stand down.
/// </summary>
public record EventFiringClaim
{
    /// <summary>The subscription whose firing this claims.</summary>
    public string SubscriptionId { get; init; } = "";

    /// <summary>The slot (the subscription's <see cref="EventSubscription.FireAt"/>) being claimed.</summary>
    public DateTimeOffset Slot { get; init; }

    /// <summary>When the claim was made. A claim older than <see cref="EventFiringClaimNodeType.Lease"/> is
    /// presumed to belong to a replica that died mid-fire and may be taken over.</summary>
    public DateTimeOffset ClaimedAt { get; init; }

    /// <summary>The process that holds the claim (diagnostic).</summary>
    public string ClaimedBy { get; init; } = "";
}

/// <summary>
/// Registers the "EventFiringClaim" MeshNode: system-managed claim nodes at
/// <c>Admin/EventFiring/{subscriptionId}_{slotTicks}</c>, beside the subscriptions they guard.
/// </summary>
public static class EventFiringClaimNodeType
{
    /// <summary>The NodeType value identifying claim nodes.</summary>
    public const string NodeType = "EventFiringClaim";

    /// <summary>Namespace of the claim nodes (Admin partition).</summary>
    public const string Namespace = "Admin/EventFiring";

    /// <summary>How long a claim is honoured before a replica may take it over as abandoned.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);

    /// <summary>The claim node id for one slot of one subscription. A takeover of an abandoned claim is a
    /// create of the NEXT generation's id (<paramref name="generation"/> &gt; 0), so a takeover is single-flight
    /// too: exactly one replica's create wins it.</summary>
    public static string IdOf(string subscriptionId, DateTimeOffset slot, int generation = 0) =>
        generation <= 0 ? $"{subscriptionId}_{slot.UtcTicks}" : $"{subscriptionId}_{slot.UtcTicks}_t{generation}";

    /// <summary>The claim node path for one slot of one subscription.</summary>
    public static string Path(string subscriptionId, DateTimeOffset slot, int generation = 0) =>
        $"{Namespace}/{IdOf(subscriptionId, slot, generation)}";

    /// <summary>Registers the claim node type.</summary>
    public static TBuilder AddEventFiringClaimType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>Creates a MeshNode definition for the EventFiringClaim node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Event Firing Claim",
        Icon = "/static/NodeTypeIcons/clock.svg",
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<EventFiringClaim>())
    };
}
