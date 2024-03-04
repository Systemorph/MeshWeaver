using OpenSmc.Collections;
using OpenSmc.Data.Persistence;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public record TypeSourceWithTypeWithDataStorage<T>(object DataSource, IMessageHub Hub, IDataStorage Storage)
    : TypeSourceWithType<T, TypeSourceWithTypeWithDataStorage<T>>(DataSource, Hub)
    where T : class
{

    protected override void UpdateImpl(IEnumerable<T> instances)
    {
        var adds = new List<T>();
        var updates = new List<T>();
        foreach (var instance in instances)
        {
            if(CurrentState.ContainsKey(GetKey(instance)))
                updates.Add(instance);
            else
                adds.Add(instance);
        }
        Storage.Add(adds);
        Storage.Update(updates);

    }

    protected override void DeleteImpl(IEnumerable<T> instances)
    {
        Storage.Delete(instances);
    }

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Initialize(await GetAsync(cancellationToken));
    }

    public async Task<IReadOnlyCollection<EntityDescriptor>> GetAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await Storage.StartTransactionAsync(cancellationToken);
        return await Storage.Query<T>().AsAsyncEnumerable().Select(e => new EntityDescriptor(CollectionName,Key(e),e)).ToArrayAsync(cancellationToken);
    }

}