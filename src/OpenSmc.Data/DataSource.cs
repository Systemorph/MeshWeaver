using System.Collections.Immutable;
using System.Reflection;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public interface IDataSource
{
    IEnumerable<ITypeSource> TypeSources { get; }
    IEnumerable<Type> MappedTypes { get; }
    object Id { get; }
    void Change(DataChangeRequest request);
    void Synchronize(DataChangedEvent @event);
    WorkspaceState GetWorkspace();
    bool ContainsInstance(object instance);
    ITypeSource GetTypeSource(Type type);
    Task InitializeAsync(CancellationToken cancellationToken);
    IReadOnlyDictionary<string, IReadOnlyDictionary<object, object>> GetData();
}

public abstract record DataSource<TDataSource, TTypeSource>(object Id, IMessageHub Hub) : IDataSource
where TDataSource : DataSource<TDataSource, TTypeSource>
where TTypeSource: ITypeSource
{

    protected virtual TDataSource This => (TDataSource)this;
    public TDataSource WithType(Type type)
        => WithType(type, x => x);



    IEnumerable<ITypeSource> IDataSource.TypeSources => TypeSources.Values.Cast<ITypeSource>();

    protected ImmutableDictionary<Type, TTypeSource> TypeSources { get; init; } = ImmutableDictionary<Type, TTypeSource>.Empty;

    public TDataSource WithTypeSource(Type type, TTypeSource typeSource)
        => This with { TypeSources = TypeSources.SetItem(type, typeSource) };


    protected virtual Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken)
        => Task.FromResult<ITransaction>(EmptyTransaction.Instance);

    public IEnumerable<Type> MappedTypes => TypeSources.Keys;

    public ITypeSource GetTypeSource(string collectionName) =>
        TypeSources.Values.FirstOrDefault(x => x.CollectionName == collectionName);


    /// <summary>
    /// Idea is to split the construction of the configuration in two parts:
    ///
    /// 1. Fluent builder to configure types, mappings, db settings, etc
    /// 2. Build step where configuration is finished. This can be used to build up services, etc.
    /// </summary>
    /// <returns></returns>


    public void Change(DataChangeRequest request)
    {
        foreach (var g in request.Elements.GroupBy(e => e.GetType()))
        {
            if (TypeSources.TryGetValue(g.Key, out var typeSource))
                typeSource.RequestChange(request with { Elements = g.ToArray() });
        }
    }

    public void Synchronize(DataChangedEvent state)
    {
        foreach (var change in state.Changes)
        {
            var typeSource = GetTypeSource(change.Collection);
            if (typeSource != null)
                typeSource.SynchronizeChange(change);
        }
    }

    //public TDataSource WithUpdate(Func<DataChangedEvent, WorkspaceState> updateAction)
    //    => This with { UpdateAction = updateAction };
    //internal Func<DataChangedEvent, WorkspaceState> UpdateAction { get; init; }

    //private WorkspaceState ApplyPatch(DataChangedPatchEvent patch)
    //{
    //    throw new NotImplementedException();
    //}


    //public void Update(IEnumerable<ChangeDescriptor> descriptors)
    //{
    //    foreach (var e in descriptors.GroupBy(x => x.Request))
    //    {
    //        var changeRequest = e.Key;
    //        foreach (var typeGroup in e.GroupBy(x => x.))
    //            ProcessRequest(changeRequest, typeGroup.Key, typeGroup.Select(x => x.Instance));
    //    }
    //}

    public WorkspaceState GetWorkspace() 
        => new(this);

    public virtual bool ContainsInstance(object instance) => TypeSources.ContainsKey(instance.GetType());

    ITypeSource IDataSource.GetTypeSource(Type type) => GetTypeSource(type);
    protected ITypeSource GetTypeSource(Type type) => TypeSources.GetValueOrDefault(type);


    public virtual TDataSource WithType(Type type, Func<ITypeSource, ITypeSource> config)
        => (TDataSource)WithTypeMethod.MakeGenericMethod(type).InvokeAsFunction(this, config);

    private static readonly MethodInfo WithTypeMethod =
        ReflectionHelper.GetMethodGeneric<DataSource<TDataSource, TTypeSource>>(x => x.WithType<object>(default));
    public TDataSource WithType<T>()
        where T : class
        => WithType<T>(d => d);

    protected abstract TDataSource WithType<T>(Func<ITypeSource, ITypeSource> config) where T : class;

    public virtual async Task InitializeAsync( CancellationToken cancellationToken)
    {
        foreach (var typeSource in TypeSources.Values)
            await typeSource.InitializeAsync(cancellationToken);
    }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<object, object>> GetData()
        => TypeSources.Values.ToDictionary
        (
            x => x.CollectionName,
            x => x.GetData()
        );

}

public record DataSource(object Id, IMessageHub Hub) : DataSource<DataSource, ITypeSource>(Id, Hub)
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
        => WithTypeSource(typeof(T), configurator.Invoke(new(Hub)));



}