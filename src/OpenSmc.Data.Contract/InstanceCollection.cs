using System.Collections.Immutable;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("OpenSmc.Data")]

namespace OpenSmc.Data;

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

    public InstanceCollection Change(DataChangedRequest request)
    {
        switch (request)
        {
            case UpdateDataRequest update:
                return Update(update.Elements.ToImmutableDictionary(GetKey, x => x));

            case DeleteDataRequest delete:
                return Delete(delete.Elements.Select(GetKey));
        }

        throw new ArgumentOutOfRangeException(nameof(request), request, null);
    }

    public IReadOnlyCollection<T> Get<T>() => Instances.Values.OfType<T>().ToArray();

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
        if (updated == null)
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
}
