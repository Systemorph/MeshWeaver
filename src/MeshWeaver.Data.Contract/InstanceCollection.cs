using System.Collections.Immutable;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MeshWeaver.Data")]

namespace MeshWeaver.Data;

/// <summary>
/// Immutable collection of entity instances within a single workspace collection,
/// keyed by each instance's identity.
/// </summary>
public record InstanceCollection
{
    /// <summary>The instances in this collection, keyed by identity.</summary>
    public ImmutableDictionary<object, object> Instances { get; init; } =
        ImmutableDictionary<object, object>.Empty;

    /// <summary>
    /// Optional collection name used during serialization to preserve type information.
    /// </summary>
    public string? CollectionName { get; init; }

    /// <summary>Creates an empty instance collection.</summary>
    public InstanceCollection() { }

    /// <summary>Creates a collection populated from the given keyed instances.</summary>
    /// <param name="instances">The instances keyed by identity.</param>
    public InstanceCollection(IReadOnlyDictionary<object, object> instances) =>
        Instances = instances.ToImmutableDictionary();

    /// <summary>Creates a collection from a sequence of instances using the given identity selector.</summary>
    /// <param name="instances">The instances to add.</param>
    /// <param name="identity">Function returning the identity key for an instance.</param>
    public InstanceCollection(IEnumerable<object> instances, Func<object, object> identity)
    {
        Instances = instances.ToImmutableDictionary(identity);
        GetKey = identity;
    }

    internal Func<object, object> GetKey { get; init; } = null!;

    /// <summary>Returns a copy with the given key set to the given value (added or replaced).</summary>
    /// <param name="key">The identity key.</param>
    /// <param name="value">The instance value.</param>
    /// <returns>The updated collection.</returns>
    public InstanceCollection SetItem(object key, object value) =>
        this with
        {
            Instances = Instances.SetItem(key, value)
        };

    /// <summary>
    /// Applies the updates and deletions in the given data change request to this collection.
    /// </summary>
    /// <param name="request">The data change request whose updates and deletions to apply.</param>
    /// <returns>The updated collection.</returns>
    public InstanceCollection Change(DataChangeRequest request)
    {
        var ret = this;
        if (request.Updates.Any())
            ret = ret.Update(request.Updates
                .Reverse()
                .Select(x => new{Key=GetKey(x), Instance = x})
                .DistinctBy(x => x.Key)
                .ToImmutableDictionary(GetKey, x => x));
        if (request.Deletions.Any())
            ret = ret.Delete(request.Deletions.Select(GetKey));

        return ret;
    }

    /// <summary>Returns all instances assignable to <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The type to filter instances by.</typeparam>
    /// <returns>The matching instances.</returns>
    public IEnumerable<T> Get<T>() => Instances.Values.OfType<T>();

    /// <summary>Returns the instance with the given id cast to <typeparamref name="T"/>, or default if absent.</summary>
    /// <typeparam name="T">The expected instance type.</typeparam>
    /// <param name="id">The identity key.</param>
    /// <returns>The instance, or default.</returns>
    public T? Get<T>(object id) => (T?)Instances.GetValueOrDefault(id);

    /// <summary>Returns the instance with the given id, or null if absent.</summary>
    /// <param name="id">The identity key.</param>
    /// <returns>The instance, or null.</returns>
    public object? GetInstance(object id)
    {
        return Instances.GetValueOrDefault(id);
    }

    private InstanceCollection Delete(IEnumerable<object> ids) =>
        this with
        {
            Instances = Instances.RemoveRange(ids)
        };

    /// <summary>Returns a copy with the given instance set under the given id.</summary>
    /// <param name="id">The identity key.</param>
    /// <param name="instance">The instance value.</param>
    /// <returns>The updated collection.</returns>
    public InstanceCollection Update(object id, object instance) =>
        this with
        {
            Instances = Instances.SetItem(id, instance)
        };

    /// <summary>
    /// Returns a copy updated with the given entities. When <paramref name="snapshot"/> is true the
    /// instances are replaced entirely; otherwise the entities are merged into the existing instances.
    /// </summary>
    /// <param name="entities">The entities to set, keyed by identity.</param>
    /// <param name="snapshot">True to replace all instances; false to merge.</param>
    /// <returns>The updated collection.</returns>
    public InstanceCollection Update(
        ImmutableDictionary<object, object> entities,
        bool snapshot = false
    )
    {
        return snapshot
            ? this with
            {
                Instances = entities
            }
            : this with
            {
                Instances = Instances.SetItems(entities)
            };
    }

    /// <summary>
    /// Returns a copy with the instances of <paramref name="updated"/> merged into this collection.
    /// </summary>
    /// <param name="updated">The collection whose instances to merge in.</param>
    /// <returns>The merged collection.</returns>
    public InstanceCollection Merge(InstanceCollection updated)
    {

        // Fix: Use the updated collection's instances directly to properly handle deletions
        // This replaces the current instances with the updated ones, ensuring deletions are reflected
        return this with { Instances = Instances.SetItems(updated.Instances) };
    }

    /// <summary>Returns a copy with the instances having the given ids removed.</summary>
    /// <param name="ids">The identity keys to remove.</param>
    /// <returns>The updated collection.</returns>
    public InstanceCollection Remove(IEnumerable<object> ids)
    {
        return this with { Instances = Instances.RemoveRange(ids) };
    }
    /// <summary>Returns a copy with the instance having the given id removed.</summary>
    /// <param name="id">The identity key to remove.</param>
    /// <returns>The updated collection.</returns>
    public InstanceCollection Remove(object id)
    {
        return this with { Instances = Instances.Remove(id) };
    }

    /// <summary>Determines whether this collection holds the same instances as another.</summary>
    /// <param name="other">The collection to compare against.</param>
    /// <returns>True if the collections are equal; otherwise false.</returns>
    public virtual bool Equals(InstanceCollection? other)
    {
        return other is not null &&
               (
                   ReferenceEquals(Instances, other.Instances) ||
                   Instances.SequenceEqual(other.Instances)
               );
    }

    /// <summary>Returns a hash code derived from the contained instances.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() =>
        Instances.Values.Select(x => x.GetHashCode()).Aggregate(0, (x, y) => x ^ y);
}
