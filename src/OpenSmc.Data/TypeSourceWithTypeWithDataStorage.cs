using System.Collections.Immutable;
using OpenSmc.Collections;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public record TypeSourceWithTypeWithDataStorage<T> : TypeSourceWithType<T, TypeSourceWithTypeWithDataStorage<T>>
    where T : class
{

    public TypeSourceWithTypeWithDataStorage(IMessageHub hub, object DataSource, IDataStorage Storage) : base(hub, DataSource)
    {
        this.Storage = Storage;
    }

    private InstanceCollection LastSaved { get; set; }

    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
    {
        var adds = instances.Instances.Where(x => !LastSaved.Instances.ContainsKey(x.Key)).Select(x => x.Value).ToArray();
        var updates = instances.Instances.Where(x => LastSaved.Instances.TryGetValue(x.Key, out var existing) && ! existing.Equals(x.Value)).Select(x => x.Value).ToArray();
        var deletes = LastSaved.Instances.Where(x => instances.Instances.ContainsKey(x)).Select(x => x.Value).ToArray();

        Storage.Add(adds);
        Storage.Update(updates);
        Storage.Delete(deletes);

        LastSaved = instances;
        return instances;
    }


    public override async Task<InstanceCollection> InitializeAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await Storage.StartTransactionAsync(cancellationToken);
        await base.InitializeAsync(cancellationToken);
        return LastSaved = new((await Storage.Query<T>().ToDictionaryAsync(GetKey, x => (object)x, cancellationToken)).ToImmutableDictionary()){GetKey = GetKey};
    }

    public IDataStorage Storage { get; init; }

}