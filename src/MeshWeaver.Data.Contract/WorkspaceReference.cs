using System.Text.Json;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

/// <summary>
/// Base type for references that describe a subset of workspace data to read or reduce.
/// </summary>
public abstract record WorkspaceReference
{
    /// <summary>
    /// Encodes a value for safe use as ONE reference/URL segment, escaping <c>.</c> as <c>%9Y</c>
    /// and <c>/</c> as <c>%9Z</c> in strings. Escaping the slash keeps a path-shaped id (e.g. a
    /// source Code node path in a NodeType shell's <c>{node}/Code/{id}</c> href) a SINGLE segment:
    /// with raw slashes the URL resolver's prefix probe contains segments like <c>Source</c>/<c>Test</c>,
    /// which the satellite-table mapping routes to the <c>code</c> table for the WHOLE probe — the
    /// node's ancestors then match nothing and navigation dies with "Page not found"
    /// (Chess/GambitHunt shell links, 2026-07-12). Same principle as the base64url node-bound
    /// DataContext (<c>LayoutAreaReference.MeshNodePrefix</c>): ids must not leak path segments.
    /// </summary>
    /// <param name="value">The value to encode.</param>
    /// <returns>The encoded value (strings escaped; other values unchanged).</returns>
    public static object Encode(object value) =>
        value is string s ? s.Replace(".", "%9Y").Replace("/", "%9Z") : value;
    /// <summary>
    /// Reverses <see cref="Encode"/>, restoring <c>/</c> from <c>%9Z</c> and <c>.</c> from
    /// <c>%9Y</c> in strings.
    /// </summary>
    /// <param name="value">The value to decode.</param>
    /// <returns>The decoded value (strings unescaped; other values unchanged).</returns>
    public static object Decode(object value) =>
        value is string s ? s.Replace("%9Z", "/").Replace("%9Y", ".") : value;

}


// ReSharper disable once UnusedTypeParameter
/// <summary>
/// A workspace reference that reduces to a value of type <typeparamref name="TReference"/>.
/// </summary>
/// <typeparam name="TReference">The type the reference reduces to.</typeparam>
public abstract record WorkspaceReference<TReference> : WorkspaceReference;

/// <summary>
/// Reference resolving to the JSON element at the given JSON pointer.
/// </summary>
/// <param name="Pointer">The JSON pointer (RFC 6901) into the value.</param>
public record JsonPointerReference(string Pointer) : WorkspaceReference<JsonElement>
{
    /// <summary>Returns the JSON pointer string.</summary>
    /// <returns>The pointer.</returns>
    public override string ToString() => Pointer;
}

/// <summary>
/// Reference to a single instance identified by its id.
/// </summary>
/// <param name="Id">The identity of the instance.</param>
public record InstanceReference(object Id) : WorkspaceReference<object>
{
    /// <summary>The JSON pointer locating the instance.</summary>
    public virtual string Pointer => $"/'{Id}'";

    /// <summary>Returns the instance pointer string.</summary>
    /// <returns>The pointer.</returns>
    public override string ToString() => Pointer;
}

/// <summary>
/// Reference to a single entity within a named collection.
/// </summary>
/// <param name="Collection">The collection the entity belongs to.</param>
/// <param name="Id">The identity of the entity.</param>
public record EntityReference(string Collection, object Id) : InstanceReference(Id)
{
    /// <summary>The JSON pointer locating the entity within its collection.</summary>
    public override string Pointer => $"/{Collection}/'{Id}'";

    /// <summary>Returns the entity pointer string.</summary>
    /// <returns>The pointer.</returns>
    public override string ToString() => Pointer;
}

/// <summary>
/// Reference to an entire named collection.
/// </summary>
/// <param name="Name">The collection name.</param>
public record CollectionReference(string Name) : WorkspaceReference<InstanceCollection>
{
    /// <summary>The JSON pointer locating the collection.</summary>
    public string Pointer => $"/{Name}";

    /// <summary>Returns the collection pointer string.</summary>
    /// <returns>The pointer.</returns>
    public override string ToString() => Pointer;
}

/// <summary>
/// Reference combining several entity-store references into one.
/// </summary>
/// <param name="References">The references to aggregate.</param>
public record AggregateWorkspaceReference(params WorkspaceReference<EntityStore>[] References)
    : WorkspaceReference<EntityStore>;

/// <summary>
/// Reference to a set of named collections, reducing to an <see cref="EntityStore"/> of just those collections.
/// </summary>
/// <param name="Collections">The names of the collections to include.</param>
public record CollectionsReference(params IReadOnlyCollection<string> Collections)
    : WorkspaceReference<EntityStore>
{

    /// <summary>Returns the comma-separated collection names.</summary>
    /// <returns>The collection names joined by commas.</returns>
    public override string ToString() => string.Join(',', Collections);

    /// <summary>Determines equality by collection-name sequence.</summary>
    /// <param name="other">The reference to compare against.</param>
    /// <returns>True if the collection sequences are equal; otherwise false.</returns>
    public virtual bool Equals(CollectionsReference? other) =>
        other != null && Collections.SequenceEqual(other.Collections);

    /// <summary>Returns a hash code derived from the collection names.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => Collections.Aggregate(17, (a, b) => a ^ b.GetHashCode());
}

/// <summary>
/// Reference combining several stream identities into a single entity-store stream.
/// </summary>
/// <param name="References">The stream identities to combine.</param>
public record CombinedStreamReference(params StreamIdentity[] References) : WorkspaceReference<EntityStore>
{
    /// <summary>Returns the comma-separated stream identities.</summary>
    /// <returns>The stream identities joined by commas.</returns>
    public override string ToString() =>
        string.Join(", ", References.Select(r => r.ToString()));
}

/// <summary>
/// Identifies a synchronization stream by its owning address and optional partition.
/// </summary>
/// <param name="Owner">The address that owns the stream.</param>
/// <param name="Partition">The partition within the owner, or null for the unpartitioned stream.</param>
public record StreamIdentity(Address Owner, object? Partition) : WorkspaceReference<EntityStore>
{
    /// <summary>Returns the owner, optionally suffixed with the partition.</summary>
    /// <returns>The string representation in the form <c>Owner</c> or <c>Owner:Partition</c>.</returns>
    public override string ToString()
    {
        return Partition == null ? Owner.ToString() : $"{Owner}:{Partition}";

    }
}

/// <summary>
/// A workspace reference scoped to a specific partition.
/// </summary>
public interface IPartitionedWorkspaceReference
{
    /// <summary>The partition the reference is scoped to, or null.</summary>
    object? Partition { get; }
    /// <summary>The inner reference being partitioned.</summary>
    WorkspaceReference Reference { get; }
}

/// <summary>
/// Wraps a reference so it resolves within a specific partition.
/// </summary>
/// <typeparam name="TReduced">The type the inner reference reduces to.</typeparam>
/// <param name="Partition">The partition to scope the reference to, or null.</param>
/// <param name="Reference">The inner reference to resolve within the partition.</param>
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

    /// <summary>Returns the unified path.</summary>
    /// <returns>The path.</returns>
    public override string ToString() => Path;
}

/// <summary>
/// Reference for the list of UCR prefixes available on a hub.
/// Returned data is a collection of prefix names ("content", "data", "schema", etc.)
/// that have dedicated autocomplete providers registered.
/// Resolved via the prefix/ UCR keyword.
/// </summary>
public record PrefixReference() : WorkspaceReference<object>
{
    /// <summary>Returns the prefix keyword.</summary>
    /// <returns>The literal <c>"prefix"</c>.</returns>
    public override string ToString() => "prefix";
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

    /// <summary>Returns the collection-qualified file path, including the partition if set.</summary>
    /// <returns>The string in the form <c>Collection/Path</c> or <c>Collection@Partition/Path</c>.</returns>
    public override string ToString() =>
        Partition != null
            ? $"{Collection}@{Partition}/{Path}"
            : $"{Collection}/{Path}";
}

/// <summary>
/// Reference for content access with collection and path.
/// Alternative naming for FileReference for semantic clarity.
/// </summary>
/// <param name="Collection">The content collection name</param>
/// <param name="Path">The path within the collection</param>
/// <param name="Partition">Optional partition for partitioned collections</param>
public record ContentWorkspaceReference(
    string Collection,
    string Path,
    string? Partition = null) : WorkspaceReference<object>
{
    /// <summary>
    /// Optional: number of rows to read (for files like Excel/CSV)
    /// </summary>
    public int? NumberOfRows { get; init; }

    /// <summary>Returns the collection-qualified content path, including the partition if set.</summary>
    /// <returns>The string in the form <c>Collection/Path</c> or <c>Collection@Partition/Path</c>.</returns>
    public override string ToString() =>
        Partition != null
            ? $"{Collection}@{Partition}/{Path}"
            : $"{Collection}/{Path}";
}

/// <summary>
/// Reference for data access via a relative path.
/// The path is relative to the current hub and will be resolved by the local path registry.
/// Path interpretation is hub-specific - the registry determines how to resolve it.
/// </summary>
/// <param name="Path">The relative data path (without prefix or address)</param>
public record DataPathReference(string Path) : WorkspaceReference<object>
{
    /// <summary>The relative data path, with any leading <c>data:</c> prefix stripped.</summary>
    public string Path { get; } = Path.StartsWith("data:") ? Path[5..] : Path;
    /// <summary>Returns the path with the <c>data:</c> prefix.</summary>
    /// <returns>The string in the form <c>data:Path</c>.</returns>
    public override string ToString() => $"data:{Path}";
}

/// <summary>
/// Reference for accessing NodeType configuration data.
/// Used by the "type" UnifiedPath handler.
/// The node type is encoded in the hub address, so this reference is just a marker.
/// Resolves to CodeConfiguration from the node's partition.
/// </summary>
public record NodeTypeReference() : WorkspaceReference<object>
{
    /// <summary>Returns the type keyword.</summary>
    /// <returns>The literal <c>"type"</c>.</returns>
    public override string ToString() => "type";
}

/// <summary>
/// Reference for accessing JSON schema for a specific type.
/// Used by the "schema" UnifiedPath handler.
/// Path format: schema[/TypeName]
/// If TypeName is empty/null, returns schema for the hub's default type.
/// </summary>
/// <param name="Type">The type name to get schema for, or null/empty for default type</param>
public record SchemaReference(string? Type = null) : WorkspaceReference<object>
{
    /// <summary>Returns the schema keyword, optionally qualified by the type name.</summary>
    /// <returns>The string <c>"schema"</c> or <c>"schema/Type"</c>.</returns>
    public override string ToString() => string.IsNullOrEmpty(Type) ? "schema" : $"schema/{Type}";
}

/// <summary>
/// Reference for accessing all data types available in a hub.
/// Used by the "model" UnifiedPath handler.
/// Returns type descriptions (name, display name, description) for all registered types.
/// Path format: model
/// </summary>
public record DataModelReference() : WorkspaceReference<object>
{
    /// <summary>Returns the model keyword.</summary>
    /// <returns>The literal <c>"model"</c>.</returns>
    public override string ToString() => "model";
}


