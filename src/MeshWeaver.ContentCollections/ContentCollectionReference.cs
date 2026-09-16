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
    // Measured on memex.systemorph.com 2026-09-16 (pod …-ztqz8, pid 1, uptime 870 min, image
    // afde4eabe0): of 312 live `sync/` hubs, 80 were duplicate mints of a value-identical
    // (host, reference) pair — and TWENTY-FOUR of them were one pair,
    // `(Doc/Architecture, collection/content)`, up from 18 thirty-four minutes earlier on the same
    // pod and pid: it mints on every read, for ever. The census compared the LIVE reference
    // objects: that group read `refsEqual=NO`, while EVERY other duplicate group — every
    // reference type with value equality — read `refsEqual=YES`.
    // See Doc/Architecture/AReferenceThatCannotBeAKey.
    //
    // <see cref="CollectionsReference"/> — the same shape, in the same role — has carried exactly
    // these two overrides all along; this is the sibling that was written without them.

    // 🚨 `null` and EMPTY are ONE reference, not two. Both mean "all collections" — `ToString()`
    // answers `collection` for either, and the reducer branches on
    // `collectionNames is null || collectionNames.Count == 0` and calls `GetAllCollectionConfigs()`
    // for both. An equality that keeps them distinct leaves the very defect this type was fixed for
    // alive on the alternating caller: `new ContentCollectionReference()` and
    // `new ContentCollectionReference([])` would be two cache keys for one logical read, hence two
    // streams and two permanent `sync/` hubs. Normalising in ONE place (`Names`) keeps `Equals`,
    // `GetHashCode` and the reducer's own predicate from drifting apart.
    private IEnumerable<string> Names() => CollectionNames ?? [];

    /// <summary>Determines equality by collection-name sequence, treating an absent and an empty
    /// name list as the same reference, so two value-identical references are interchangeable as a
    /// cache key.</summary>
    /// <param name="other">The reference to compare against.</param>
    /// <returns>True if the name sequences are equal element-wise; otherwise false.</returns>
    public virtual bool Equals(ContentCollectionReference? other) =>
        other is not null && Names().SequenceEqual(other.Names());

    /// <summary>Returns a hash code derived from the collection names; an absent and an empty name
    /// list hash alike, as their equality requires.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() =>
        Names().Aggregate(17, (a, b) => a ^ b.GetHashCode());
}
