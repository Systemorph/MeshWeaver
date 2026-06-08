using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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

    /// <summary>
    /// IDataStorage is a pure DB abstraction (transactions + IQueryable backed by EF or
    /// in-memory) — a genuine DB I/O leaf. Returns <see cref="IObservable{T}"/> (never Task):
    /// Defer keeps it cold (the query runs on Subscribe) and the leaf's EF Task is bridged
    /// reactively with <c>.ToObservable()</c> — no Observable.FromAsync.
    /// </summary>
    protected override IObservable<InstanceCollection> Initialize(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken
    ) => Observable.Defer(() => LoadFromStorageAsync().ToObservable());

    private async Task<InstanceCollection> LoadFromStorageAsync()
    {
        await using var transaction = await Storage.StartTransactionAsync(CancellationToken.None).ConfigureAwait(false);
        return LastSaved = new()
        {
            Instances = (
                await Storage
                    .Query<T>()
                    .ToDictionaryAsync(TypeDefinition.GetKey, x => (object)x, CancellationToken.None)
                    .ConfigureAwait(false)
            ).ToImmutableDictionary(),
            GetKey = TypeDefinition.GetKey
        };
    }

    public IDataStorage Storage { get; init; }
}
