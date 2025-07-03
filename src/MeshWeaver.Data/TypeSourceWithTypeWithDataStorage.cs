using System.Collections.Immutable;
using MeshWeaver.Utils;

namespace MeshWeaver.Data;

public record TypeSourceWithTypeWithDataStorage<T>
    : TypeSourceWithType<T, TypeSourceWithTypeWithDataStorage<T>>
    where T : class
{
    public TypeSourceWithTypeWithDataStorage(
        IWorkspace Workspace,
        object DataSource,
        IDataStorage Storage
    )
        : base(Workspace, DataSource)
    {
        this.Storage = Storage;
    }

    private InstanceCollection LastSaved { get; set; } = new();

    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
    {
        var adds = instances
            .Instances.Where(x => !LastSaved.Instances.ContainsKey(x.Key))
            .Select(x => x.Value)
            .ToArray();
        var updates = instances
            .Instances.Where(x =>
                LastSaved.Instances.TryGetValue(x.Key, out var existing)
                && !existing.Equals(x.Value)
            )
            .Select(x => x.Value)
            .ToArray();
        var deletes = LastSaved
            .Instances.Where(x => instances.Instances.ContainsKey(x))
            .Select(x => x.Value)
            .ToArray();

        Storage.Add(adds);
        Storage.Update(updates);
        Storage.Delete(deletes);

        LastSaved = instances;
        return instances;
    }

    protected override async Task<InstanceCollection> InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await Storage.StartTransactionAsync(cancellationToken);
        return LastSaved = new()
        {
            Instances = (
                await Storage
                    .Query<T>()
                    .ToDictionaryAsync(TypeDefinition.GetKey, x => (object)x, cancellationToken)
            ).ToImmutableDictionary(),
            GetKey = TypeDefinition.GetKey
        };
    }

    public IDataStorage Storage { get; init; }
}
