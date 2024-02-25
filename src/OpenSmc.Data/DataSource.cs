using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenSmc.Data.Persistence;
using OpenSmc.Messaging;
using OpenSmc.Reflection;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Data;

public interface IDataSource
{
    IEnumerable<ITypeSource> TypeSources { get; }
    IEnumerable<Type> MappedTypes { get; }
    object Id { get; }
    IEnumerable<DataSourceUpdate> Change(DataChangeRequest request);
    //Task SynchronizeAsync(DataChangedEvent @event, CancellationToken cancellationToken);
    bool ContainsInstance(object instance);
    ITypeSource GetTypeSource(Type type);
    Task InitializeAsync(CancellationToken cancellationToken);
    IReadOnlyDictionary<string, IReadOnlyCollection<EntityDescriptor>> GetData();
    Task UpdateAsync(IEnumerable<DataSourceUpdate> update, CancellationToken cancellationToken);
}

public abstract record DataSource<TDataSource>(object Id, IMessageHub Hub) : IDataSource
where TDataSource : DataSource<TDataSource>
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

    public async Task UpdateAsync(IEnumerable<DataSourceUpdate> updates, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await StartTransactionAsync(cancellationToken);
            foreach (var g in updates.GroupBy(x => x.Collection))
            {
                var typeSource = GetTypeSource(g.Key);
                typeSource.Update(g);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogError("Error committing data transaction: {exception}", e);
        }
    }

    public IEnumerable<DataSourceUpdate> Change(DataChangeRequest request)
    {
        return request.Elements.GroupBy(e => e.GetType())
            .SelectMany(g =>
                TypeSources.GetValueOrDefault(g.Key)?.RequestChange(request with { Elements = g.ToArray() }) 
                ?? Enumerable.Empty<DataSourceUpdate>());
    }




    public virtual bool ContainsInstance(object instance) => TypeSources.ContainsKey(instance.GetType());

    ITypeSource IDataSource.GetTypeSource(Type type) => GetTypeSource(type);
    protected ITypeSource GetTypeSource(Type type) => TypeSources.GetValueOrDefault(type);


    public virtual TDataSource WithType(Type type, Func<ITypeSource, ITypeSource> config)
        => (TDataSource)WithTypeMethod.MakeGenericMethod(type).InvokeAsFunction(this, config);

    private static readonly MethodInfo WithTypeMethod =
        ReflectionHelper.GetMethodGeneric<DataSource<TDataSource>>(x => x.WithType<object>(default));
    public TDataSource WithType<T>()
        where T : class
        => WithType<T>(d => d);

    protected abstract TDataSource WithType<T>(Func<ITypeSource, ITypeSource> config) where T : class;

    public virtual async Task InitializeAsync( CancellationToken cancellationToken)
    {
        foreach (var typeSource in TypeSources.Values)
            await typeSource.InitializeAsync(cancellationToken);
    }

    public IReadOnlyDictionary<string, IReadOnlyCollection<EntityDescriptor>> GetData()
        => TypeSources.Values.ToDictionary(ts => ts.CollectionName, ts => ts.GetData());

    public Task UpdateAsync(DataSourceUpdate update, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
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
        => WithTypeSource(typeof(T), configurator.Invoke(new(Id, Hub)));


}