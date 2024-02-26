using OpenSmc.Collections;
using OpenSmc.Data.Persistence;
using OpenSmc.Messaging;

namespace OpenSmc.Data;

public record TypeSourceWithTypeWithDataStorage<T>(object DataSource, IMessageHub Hub, IDataStorage Storage)
    : TypeSourceWithType<T, TypeSourceWithTypeWithDataStorage<T>>(DataSource, Hub)
    where T : class
{



    protected virtual void AddImpl(IEnumerable<T> instances)
    {
        Storage.Add(instances);
    }

    protected override void UpdateImpl(IEnumerable<T> instances)
    {
        Storage.Update(instances);
    }

    protected override void DeleteImpl(IEnumerable<T> instances)
    {
        Storage.Delete(instances);
    }

    public override async Task<IReadOnlyCollection<EntityDescriptor>> GetAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await Storage.StartTransactionAsync(cancellationToken);
        return await Storage.Query<T>().AsAsyncEnumerable().Select(e => new EntityDescriptor(CollectionName,Key(e),e)).ToArrayAsync(cancellationToken);
    }

}