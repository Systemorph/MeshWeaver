using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenSmc.Messaging;
using OpenSmc.Reflection;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Data;

public interface IDataSource : IDisposable
{
    IEnumerable<ITypeSource> TypeSources { get; }
    IEnumerable<Type> MappedTypes { get; }
    object Id { get; }
    IReadOnlyCollection<DataChangeRequest> Change(DataChangeRequest request);
    Task<WorkspaceState> InitializeAsync(CancellationToken cancellationToken);
    Task UpdateAsync(IEnumerable<DataChangeRequest> update, CancellationToken cancellationToken);
    object MapInstanceToPartition(object instance);
}

public abstract record DataSource<TDataSource>(object Id, IMessageHub Hub) : IDataSource, IAsyncDisposable where TDataSource : DataSource<TDataSource>
{ 
    protected virtual TDataSource This => (TDataSource)this;
    public TDataSource WithType(Type type)
        => WithType(type, x => x);

    [Inject] private ILogger<DataSource<TDataSource>> logger;

    IEnumerable<ITypeSource> IDataSource.TypeSources => TypeSources.Values;

    protected ImmutableDictionary<Type, ITypeSource> TypeSources { get; init; } = ImmutableDictionary<Type, ITypeSource>.Empty;

    public TDataSource WithTypeSource(Type type, ITypeSource typeSource)
        => This with
        {
            TypeSources = TypeSources.SetItem(type, typeSource)
        };


    protected virtual Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken)
        => Task.FromResult<ITransaction>(EmptyTransaction.Instance);

    public IEnumerable<Type> MappedTypes => TypeSources.Keys;

    public ITypeSource GetTypeSource(string collectionName) =>
        TypeSources.Values.FirstOrDefault(x => x.CollectionName == collectionName);


    public async Task UpdateAsync(IEnumerable<DataChangeRequest> updates, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await StartTransactionAsync(cancellationToken);
            foreach (var change in updates)
            {
                Change(change);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogError("Error committing data transaction: {exception}", e);
        }
    }

    public object MapInstanceToPartition(object instance)
    {
        if(TypeSources.TryGetValue(instance.GetType(), out var typeSource))
            return typeSource.GetPartition(instance);
        return null;
    }

    public virtual IReadOnlyCollection<DataChangeRequest> Change(DataChangeRequest request)
    {
        if (request is DataChangeRequestWithElements requestWithElements)
            return Change(requestWithElements);

        throw new ArgumentOutOfRangeException($"No implementation found for {request.GetType().FullName}");
    }



    public ITypeSource GetTypeSource(Type type) => TypeSources.GetValueOrDefault(type);


    public virtual TDataSource WithType(Type type, Func<ITypeSource, ITypeSource> config)
        => (TDataSource)WithTypeMethod.MakeGenericMethod(type).InvokeAsFunction(this, config);

    private static readonly MethodInfo WithTypeMethod =
        ReflectionHelper.GetMethodGeneric<DataSource<TDataSource>>(x => x.WithType<object>(default));
    public TDataSource WithType<T>()
        where T : class
        => WithType<T>(d => d);

    protected abstract TDataSource WithType<T>(Func<ITypeSource, ITypeSource> config) where T : class;

    public virtual async Task<WorkspaceState> InitializeAsync( CancellationToken cancellationToken)
    {
        return new WorkspaceState(Hub,
            await TypeSources
            .Values
            .ToAsyncEnumerable()
            .SelectAwait(async ts => new { TypeSource = ts, Instances = await ts.InitializeAsync(cancellationToken) })
            .ToDictionaryAsync(x => x.TypeSource.CollectionName,
                x => new InstancesInCollection(x.Instances), cancellationToken: cancellationToken),
            TypeSources
            );
    }


    public virtual void Dispose()
    {
    }

    public async ValueTask DisposeAsync()
    {
        if (Hub != null) await Hub.DisposeAsync();
    }
}

public record DataSource(object Id, IMessageHub Hub) : DataSource<DataSource>(Id, Hub)
{
    public DataSource WithTransaction(Func<CancellationToken, Task<ITransaction>> startTransaction)
        => this with { StartTransactionAction = startTransaction };
    internal Func<CancellationToken, Task<ITransaction>> StartTransactionAction { get; init; }
        = _ => Task.FromResult<ITransaction>(EmptyTransaction.Instance);

    protected override Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken)
        => StartTransactionAction(cancellationToken);


    protected override DataSource WithType<T>(Func<ITypeSource, ITypeSource> config)
    => WithType<T>(x => (TypeSourceWithType<T>)config(x));



    public DataSource WithType<T>(
        Func<TypeSourceWithType<T>, TypeSourceWithType<T>> configurator)
        where T : class
        => WithTypeSource(typeof(T), configurator.Invoke(new(Id, Hub.ServiceProvider)));


}