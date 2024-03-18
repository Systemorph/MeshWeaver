using System.Runtime.CompilerServices;
using System.Collections.Immutable;

[assembly:InternalsVisibleTo("OpenSmc.Data")]


namespace OpenSmc.Data;

public record InstancesInCollection(ImmutableDictionary<object, object> Instances)
{
    internal Func<object,object> GetKey { get; init; }
    public InstancesInCollection Change(DataChangeRequest request)
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



    public IReadOnlyCollection<T> Get<T>()
        => Instances.Values.OfType<T>().ToArray();

    public T Get<T>(object id)
        => (T)Instances.GetValueOrDefault(id);








    public object GetData(object id)
    {
        return Instances.GetValueOrDefault(id);
    }



    private InstancesInCollection Delete(IEnumerable<object> ids) =>
        this with
        {
            Instances = Instances.RemoveRange(ids)
        };


    private InstancesInCollection Update(ImmutableDictionary<object, object> entities, bool snapshot = false)
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

    public InstancesInCollection Merge(InstancesInCollection other)
    {
        if (other == null)
            return this;
        return this with
        {
            Instances = other.Instances.SetItems(Instances)
        };
    }
}