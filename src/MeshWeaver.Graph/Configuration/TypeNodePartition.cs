namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Represents all partition objects from a NodeType node.
/// Used for compilation to collect all DataModels, LayoutAreas, and HubFeatures
/// with their modification timestamps for cache validation.
/// </summary>
public record TypeNodePartition
{
    /// <summary>
    /// All DataModel definitions from the partition.
    /// A type node can have multiple DataModels.
    /// </summary>
    public IReadOnlyList<DataModel> DataModels { get; init; } = [];

    /// <summary>
    /// All LayoutAreaConfig definitions from the partition.
    /// </summary>
    public IReadOnlyList<LayoutAreaConfig> LayoutAreas { get; init; } = [];

    /// <summary>
    /// The HubFeatureConfig from the partition, if any.
    /// </summary>
    public HubFeatureConfig? HubFeatures { get; init; }

    /// <summary>
    /// The newest modification timestamp across all partition objects.
    /// Used for cache invalidation.
    /// </summary>
    public DateTimeOffset NewestTimestamp { get; init; }

    /// <summary>
    /// Whether this partition has any compilable content.
    /// Even without DataModels, we may need to compile for HubConfiguration only.
    /// </summary>
    public bool HasContent => DataModels.Count > 0 || LayoutAreas.Count > 0 || HubFeatures != null;
}
