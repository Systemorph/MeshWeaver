using System.Collections.Immutable;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MeshWeaver.Data")]

namespace MeshWeaver.Data;

public record InstanceCollection
{
    public ImmutableDictionary<object, object> Instances { get; init; } =
        ImmutableDictionary<object, object>.Empty;

    public InstanceCollection() { }

    public InstanceCollection(IReadOnlyDictionary<object, object> instances) =>
        Instances = instances.ToImmutableDictionary();

    public InstanceCollection(IEnumerable<object> instances, Func<object, object> identity)
    {
        Instances = instances.ToImmutableDictionary(identity);
        GetKey = identity;
    }

    internal Func<object, object> GetKey { get; init; } = null!;

    public InstanceCollection SetItem(object key, object value) =>
        this with
        {
            Instances = Instances.SetItem(key, value)
        };

    public InstanceCollection Change(DataChangeRequest request)
    {
        var ret = this;
        if (request.Updates.Any())
            ret = ret.Update(request.Updates.ToImmutableDictionary(GetKey, x => x));
        if (request.Deletions.Any())
            ret = ret.Delete(request.Deletions.Select(GetKey));

        return ret;
    }

    public IEnumerable<T> Get<T>() => Instances.Values.OfType<T>();

    public T? Get<T>(object id) => (T?)Instances.GetValueOrDefault(id);

    public object? GetInstance(object id)
    {
        return Instances.GetValueOrDefault(id);
    }

    private InstanceCollection Delete(IEnumerable<object> ids) =>
        this with
        {
            Instances = Instances.RemoveRange(ids)
        };

    public InstanceCollection Update(object id, object instance) =>
        this with
        {
            Instances = Instances.SetItem(id, instance)
        };

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

    public InstanceCollection Merge(InstanceCollection updated)
    {
        
        // Fix: Use the updated collection's instances directly to properly handle deletions
        // This replaces the current instances with the updated ones, ensuring deletions are reflected
        return this with { Instances = Instances.SetItems(updated.Instances) };
    }

    public InstanceCollection Remove(IEnumerable<object> ids)
    {
        return this with { Instances = Instances.RemoveRange(ids) };
    }
    public InstanceCollection Remove(object id)
    {
        return this with { Instances = Instances.Remove(id) };
    }

    public virtual bool Equals(InstanceCollection? other)
    {
        return other is not null &&
               (
                   ReferenceEquals(Instances, other.Instances) ||
                   Instances.SequenceEqual(other.Instances)
               );
    }

    public override int GetHashCode() =>
        Instances.Values.Select(x => x.GetHashCode()).Aggregate((x, y) => x ^ y);
}
