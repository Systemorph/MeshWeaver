#nullable enable
using System.Text.Json;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

public abstract record WorkspaceReference
{
    public static object Encode(object value) => value is string s ? s.Replace(".", "%9Y") : value;
    public static object Decode(object value) => value is string s ? s.Replace("%9Y", ".") : value;

}


// ReSharper disable once UnusedTypeParameter
public abstract record WorkspaceReference<TReference> : WorkspaceReference;

public record JsonPointerReference(string Pointer) : WorkspaceReference<JsonElement>
{
    public override string ToString() => Pointer;
}

public record InstanceReference(object Id) : WorkspaceReference<object>
{
    public virtual string Pointer => $"/'{Id}'";

    public override string ToString() => Pointer;
}

public record EntityReference(string Collection, object Id) : InstanceReference(Id)
{
    public override string Pointer => $"/{Collection}/'{Id}'";

    public override string ToString() => Pointer;
}

public record CollectionReference(string Name) : WorkspaceReference<InstanceCollection>
{
    public string Pointer => $"/{Name}";

    public override string ToString() => Pointer;
}

public record AggregateWorkspaceReference(params WorkspaceReference<EntityStore>[] References)
    : WorkspaceReference<EntityStore>;

public record CollectionsReference(params IReadOnlyCollection<string> Collections)
    : WorkspaceReference<EntityStore>
{

    public override string ToString() => string.Join(',', Collections);

    public virtual bool Equals(CollectionsReference? other) =>
        other != null && Collections.SequenceEqual(other.Collections);

    public override int GetHashCode() => Collections.Aggregate(17, (a, b) => a ^ b.GetHashCode());
}

public record CombinedStreamReference(params StreamIdentity[] References) : WorkspaceReference<EntityStore>
{
    public override string ToString() => 
        string.Join(", ", References.Select(r => r.ToString()));
}

public record StreamIdentity(Address Owner, object? Partition) : WorkspaceReference<EntityStore>
{
    public override string ToString()
    {
        return Partition == null ? Owner.ToString() : $"{Owner}:{Partition}";

    }
}

public interface IPartitionedWorkspaceReference
{
    object? Partition { get; }
    WorkspaceReference Reference { get; }
}

public record PartitionedWorkspaceReference<TReduced>(object? Partition, WorkspaceReference<TReduced> Reference)
    : WorkspaceReference<TReduced>, IPartitionedWorkspaceReference
{
    WorkspaceReference IPartitionedWorkspaceReference.Reference => Reference;
}

/// <summary>
/// Unified reference for accessing content via path patterns.
/// Supports multiple path formats:
/// - Data: data:addressType/addressId[/collection[/entityId]]
/// - Content: content:addressType/addressId/collection/path
/// - Area: area:addressType/addressId/areaName[/areaId]
/// </summary>
/// <param name="Path">The unified path to access content</param>
public record UnifiedReference(string Path) : WorkspaceReference<object>
{
    /// <summary>
    /// Optional: number of rows to read (for files like Excel/CSV)
    /// </summary>
    public int? NumberOfRows { get; init; }

    /// <summary>
    /// Parses the path into a ContentReference.
    /// </summary>
    public ContentReference ParsedReference => ContentReference.Parse(Path);

    public override string ToString() => Path;
}

/// <summary>
/// Reference to a file in a content collection.
/// Used by the content: prefix handler.
/// </summary>
/// <param name="Collection">The content collection name</param>
/// <param name="Path">The path to the file within the collection</param>
/// <param name="Partition">Optional partition for partitioned collections</param>
public record FileReference(
    string Collection,
    string Path,
    string? Partition = null) : WorkspaceReference<object>
{
    /// <summary>
    /// Optional: number of rows to read (for files like Excel/CSV)
    /// </summary>
    public int? NumberOfRows { get; init; }

    public override string ToString() =>
        Partition != null
            ? $"{Collection}@{Partition}/{Path}"
            : $"{Collection}/{Path}";
}

