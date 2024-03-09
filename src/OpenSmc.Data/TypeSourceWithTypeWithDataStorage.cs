using System.Collections.Immutable;
using OpenSmc.Collections;

namespace OpenSmc.Data;

public record TypeSourceWithTypeWithDataStorage<T> : TypeSourceWithType<T, TypeSourceWithTypeWithDataStorage<T>>
    where T : class
{

    public TypeSourceWithTypeWithDataStorage(object DataSource, IServiceProvider serviceProvider, IDataStorage Storage) : base(DataSource, serviceProvider)
    {
        this.Storage = Storage;
    }

    private ImmutableDictionary<object,object> LastSaved { get; set; }

    public override void UpdateImpl(InstancesInCollection instances)
    {
        var adds = instances.Instances.Where(x => !LastSaved.ContainsKey(x.Key)).Select(x => x.Value).ToArray();
        var updates = instances.Instances.Where(x => LastSaved.TryGetValue(x.Key, out var existing) && ! existing.Equals(x.Value)).Select(x => x.Value).ToArray();
        var deletes = LastSaved.Where(x => instances.Instances.ContainsKey(x)).Select(x => x.Value).ToArray();

        Storage.Add(adds);
        Storage.Update(updates);
        Storage.Delete(deletes);

        LastSaved = instances.Instances;
    }


    public override async Task<ImmutableDictionary<object, object>> InitializeAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await Storage.StartTransactionAsync(cancellationToken);
        return LastSaved = (await Storage.Query<T>().ToDictionaryAsync(GetKey, x => (object)x, cancellationToken)).ToImmutableDictionary();
    }

    public IDataStorage Storage { get; init; }

}