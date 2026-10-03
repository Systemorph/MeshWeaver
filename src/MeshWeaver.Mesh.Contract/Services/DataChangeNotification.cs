using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// One emission on <see cref="IStorageAdapter.Changes"/> — describes a single
/// commit (Created / Updated / Deleted) at <paramref name="Path"/>. Subscribers
/// (synced-query providers, etc.) react to this to re-emit their downstream
/// observables.
///
/// <para>Carries the post-commit <paramref name="Entity"/> when available so
/// subscribers can pattern-match without an extra read; <c>null</c> for
/// <see cref="DataChangeKind.Deleted"/> events from backends that don't
/// retain a tombstone payload.</para>
/// </summary>
public record DataChangeNotification(
    string Path,
    DataChangeKind Kind,
    object? Entity,
    DateTimeOffset Timestamp
)
{
    private static readonly JsonSerializerOptions EntityOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Node type carried by the storage backend when it is available without reading the row.
    /// Cross-process feeds may leave this <see langword="null"/> while an older backend payload is
    /// still deployed; consumers must then treat the notification as unclassified or re-read it.
    /// </summary>
    public string? NodeType { get; init; }

    /// <summary>
    /// Durable node version carried by the storage backend, or <see langword="null"/> when the
    /// backend's notification payload predates version metadata.
    /// </summary>
    public long? Version { get; init; }

    /// <summary>
    /// True when the publisher KNOWS what the path held before this commit, so
    /// <see cref="PreviousNodeType"/> is authoritative: <see langword="null"/> there then means
    /// "there was no row". False (the default) means the prior state is unknown — every backend
    /// whose feed cannot see the row it replaced — and a consumer must not reason about it.
    /// </summary>
    public bool PriorStateKnown { get; init; }

    /// <summary>
    /// The node type the path held BEFORE this commit — meaningful only when
    /// <see cref="PriorStateKnown"/> is true. A retype (a package root installed as a placeholder
    /// Space, then written as its package type) changes a node's membership in a
    /// <c>nodeType:</c>-constrained query in BOTH directions, so a consumer that prunes change
    /// triggers by node type needs the type the row is leaving as well as the one it arrives with.
    /// </summary>
    public string? PreviousNodeType { get; init; }

    /// <summary>
    /// Stamps the prior state onto this notification. <paramref name="previous"/> is the row the
    /// commit replaced, or <see langword="null"/> when the commit inserted a new row.
    /// </summary>
    /// <param name="previous">The replaced row, or <see langword="null"/> for an insert.</param>
    /// <returns>The notification with <see cref="PriorStateKnown"/> set.</returns>
    public DataChangeNotification WithPriorState(MeshNode? previous) =>
        this with { PriorStateKnown = true, PreviousNodeType = previous?.NodeType };

    /// <summary>Factory for a Create commit notification.</summary>
    public static DataChangeNotification Created(string path, object? entity) =>
        WithEntityMetadata(new(
            NormalizePath(path), DataChangeKind.Created, entity, DateTimeOffset.UtcNow));

    /// <summary>Factory for an Update commit notification.</summary>
    public static DataChangeNotification Updated(string path, object? entity) =>
        WithEntityMetadata(new(
            NormalizePath(path), DataChangeKind.Updated, entity, DateTimeOffset.UtcNow));

    /// <summary>Factory for a Delete commit notification — <paramref name="entity"/> may be null for backends that don't retain a tombstone payload.</summary>
    public static DataChangeNotification Deleted(string path, object? entity = null) =>
        WithEntityMetadata(new(
            NormalizePath(path), DataChangeKind.Deleted, entity, DateTimeOffset.UtcNow));

    private static DataChangeNotification WithEntityMetadata(DataChangeNotification notification)
    {
        var node = notification.Entity.As<MeshNode>(EntityOptions);
        return node is not null
            ? notification with { NodeType = node.NodeType, Version = node.Version }
            : notification;
    }

    private static string NormalizePath(string? path) =>
        path?.Trim('/') ?? "";
}

/// <summary>Kind of commit a <see cref="DataChangeNotification"/> describes.</summary>
public enum DataChangeKind
{
    /// <summary>Newly inserted entity.</summary>
    Created,
    /// <summary>Existing entity replaced or patched.</summary>
    Updated,
    /// <summary>Entity removed from storage.</summary>
    Deleted
}
