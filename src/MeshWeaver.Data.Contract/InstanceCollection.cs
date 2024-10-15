﻿using System.Collections.Immutable;
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

    internal Func<object, object> GetKey { get; init; }

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
            ret = ret.Delete(request.Updates.Select(GetKey));

        return ret;
    }

    public IEnumerable<T> Get<T>() => Instances.Values.OfType<T>();

    public T Get<T>(object id) => (T)Instances.GetValueOrDefault(id);

    public object GetData(object id)
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
        if (updated is null)
            return this;
        return this with
        {
            //TODO Roland Bürgi 2024-05-10: this won't work for deletions ==> need to create unit test and implement deletion via sync
            Instances = Instances.SetItems(updated.Instances)
        };
    }

    public InstanceCollection Remove(IEnumerable<object> ids)
    {
        return this with { Instances = Instances.RemoveRange(ids) };
    }

    public virtual bool Equals(InstanceCollection other)
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
