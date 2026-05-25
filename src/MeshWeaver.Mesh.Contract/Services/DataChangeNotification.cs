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
    public static DataChangeNotification Created(string path, object? entity) =>
        new(NormalizePath(path), DataChangeKind.Created, entity, DateTimeOffset.UtcNow);

    public static DataChangeNotification Updated(string path, object? entity) =>
        new(NormalizePath(path), DataChangeKind.Updated, entity, DateTimeOffset.UtcNow);

    public static DataChangeNotification Deleted(string path, object? entity = null) =>
        new(NormalizePath(path), DataChangeKind.Deleted, entity, DateTimeOffset.UtcNow);

    private static string NormalizePath(string? path) =>
        path?.Trim('/') ?? "";
}

public enum DataChangeKind
{
    Created,
    Updated,
    Deleted
}
