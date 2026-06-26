namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Marker registration indicating a partition to include in selective partitioned persistence.
/// Register multiple instances via DI; collected by IEnumerable&lt;PartitionInclusion&gt;.
/// </summary>
public record PartitionInclusion(string Name);

/// <summary>
/// Filters partitions based on an explicit inclusion list.
/// When no inclusions are registered, all partitions pass (backward compatibility).
/// </summary>
public class PartitionFilter(IEnumerable<string> includedPartitions)
{
    private readonly HashSet<string> _included = new(includedPartitions, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Decides whether a partition is included by this filter: returns
    /// <c>true</c> when no inclusions are registered (all pass) or when
    /// <paramref name="name"/> is in the inclusion list (case-insensitive).
    /// </summary>
    /// <param name="name">The partition name to test.</param>
    /// <returns><c>true</c> if the partition should be included; otherwise <c>false</c>.</returns>
    public bool ShouldInclude(string name) => _included.Count == 0 || _included.Contains(name);
}
