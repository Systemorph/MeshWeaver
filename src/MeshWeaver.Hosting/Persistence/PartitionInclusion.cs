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

    public bool ShouldInclude(string name) => _included.Count == 0 || _included.Contains(name);
}
