using MeshWeaver.Data;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Reference for accessing content collection configurations.
/// Used by the "collection" UnifiedPath handler.
/// Path format: collection[/CollectionName]
/// If CollectionName is provided, returns configuration for that specific collection.
/// If CollectionName is empty/null, returns all collection configurations.
/// </summary>
/// <param name="CollectionNames">Optional collection names to filter, or null/empty for all collections</param>
public record ContentCollectionReference(params IReadOnlyCollection<string>? CollectionNames) : WorkspaceReference<object>
{
    /// <summary>
    /// Renders the reference as its path form: <c>collection/name1,name2</c> when names are
    /// specified, otherwise just <c>collection</c> (all collections).
    /// </summary>
    /// <returns>The string path representation of this reference.</returns>
    public override string ToString() =>
        CollectionNames is { Count: > 0 }
            ? $"collection/{string.Join(",", CollectionNames)}"
            : "collection";

    // 🚨 A WorkspaceReference IS A CACHE KEY, and a positional record whose member is a COLLECTION
    // does not get value equality for free (Systemorph/MeshWeaver#3432). The compiler-generated
    // Equals/GetHashCode use EqualityComparer<IReadOnlyCollection<string>>.Default, which for a
    // string[] is REFERENCE equality — so two `new ContentCollectionReference(["content"])` are
    // never Equals and never share a hash. `Workspace._localStreamCache` is keyed on the reference,
    // so every read MISSED the cache, and every miss constructs a SynchronizationStream, hence a
    // hosted `sync/{id}` sub-hub with its own Autofac scope, TypeRegistry and JsonSerializerOptions
    // (~390 KB), registered for disposal on the HUB-LIFETIME node hub. One permanent hub per read.
    //
    // Measured on memex.systemorph.com 2026-09-16 (pod …-ztqz8, pid 1, uptime 831 min, image
    // afde4eabe0): of 311 live `sync/` hubs, 63 were duplicate mints of a value-identical
    // (host, reference) pair — and EIGHTEEN of them were one pair,
    // `(Doc/Architecture, collection/content)`. The census compared the LIVE reference objects:
    // that group read `refsEqual=NO`, while all 21 other duplicate groups — every reference type
    // with value equality — read `refsEqual=YES`. See Doc/Architecture/AReferenceThatCannotBeAKey.
    //
    // <see cref="CollectionsReference"/> — the same shape, in the same role — has carried exactly
    // these two overrides all along; this is the sibling that was written without them.

    /// <summary>Determines equality by collection-name sequence, so two value-identical references
    /// are interchangeable as a cache key.</summary>
    /// <param name="other">The reference to compare against.</param>
    /// <returns>True if both name sequences are absent, or equal element-wise; otherwise false.</returns>
    public virtual bool Equals(ContentCollectionReference? other) =>
        other is not null
        && (CollectionNames is null
            ? other.CollectionNames is null
            : other.CollectionNames is not null && CollectionNames.SequenceEqual(other.CollectionNames));

    /// <summary>Returns a hash code derived from the collection names.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() =>
        CollectionNames is null ? 0 : CollectionNames.Aggregate(17, (a, b) => a ^ b.GetHashCode());
}
