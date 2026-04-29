using System.Collections.Immutable;
using System.Reactive.Linq;
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
    /// IDataStorage is a pure DB abstraction (transactions + IQueryable backed by EF
    /// or in-memory) — no hub round-trips, so wrapping with
    /// <see cref="Observable.FromAsync{TResult}(Func{CancellationToken,Task{TResult}})"/>
    /// is sanctioned per <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </summary>
    protected override IObservable<InstanceCollection> Initialize(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken
    ) => Observable.FromAsync(async ct =>
    {
        await using var transaction = await Storage.StartTransactionAsync(ct);
        return LastSaved = new()
        {
            Instances = (
                await Storage
                    .Query<T>()
                    .ToDictionaryAsync(TypeDefinition.GetKey, x => (object)x, ct)
            ).ToImmutableDictionary(),
            GetKey = TypeDefinition.GetKey
        };
    });

    public IDataStorage Storage { get; init; }
}
