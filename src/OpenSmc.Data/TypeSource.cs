using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Reflection;
using OpenSmc.Serialization;

namespace OpenSmc.Data;

public abstract record TypeSource<TTypeSource> : ITypeSource
    where TTypeSource : TypeSource<TTypeSource>
{

    protected TypeSource(IMessageHub hub, Type ElementType, object DataSource)
    {
        this.ElementType = ElementType;
        this.DataSource = DataSource;
        var typeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>().WithType(ElementType);
        CollectionName = typeRegistry.TryGetTypeName(ElementType, out var typeName) ? typeName : ElementType.FullName;
        Key = GetKeyFunction(ElementType);
    }

    ITypeSource ITypeSource.WithKey(Func<object, object> key)
        => This with { Key = key };

    public virtual object GetKey(object instance)
        => Key(instance);

    protected Func<object, object> Key { get; init; }
    private static Func<object, object> GetKeyFunction(Type elementType)
    {
        var keyProperty = elementType?.GetProperties().SingleOrDefault(p => p.HasAttribute<KeyAttribute>());
        if (keyProperty == null)
            keyProperty = elementType?.GetProperties().SingleOrDefault(x => x.Name.ToLowerInvariant() == "id");
        if (keyProperty == null)
            return null;
        var prm = Expression.Parameter(typeof(object));
        return Expression
            .Lambda<Func<object, object>>(Expression.Convert(Expression.Property(Expression.Convert(prm, elementType), keyProperty), typeof(object)), prm)
            .Compile();
    }



    protected TTypeSource This => (TTypeSource)this;

    



    public virtual InstanceCollection Update(WorkspaceState workspace)
    {
        var myCollection = workspace.Reduce(new CollectionReference(CollectionName));

        return UpdateImpl(myCollection);
    }

    private IDisposable workspaceSubscription;


    

    protected virtual InstanceCollection UpdateImpl(InstanceCollection myCollection) => myCollection;


    ITypeSource ITypeSource.WithInitialData(
        Func<CancellationToken, Task<IEnumerable<object>>> initialization)
        => WithInitialData(initialization);

    public TTypeSource WithInitialData(Func<CancellationToken, Task<IEnumerable<object>>> initialization)
        => This with { InitializationFunction = initialization };

    protected Func<CancellationToken, Task<IEnumerable<object>>> InitializationFunction { get; init; }
        = _ => Task.FromResult(Enumerable.Empty<object>());


    public Type ElementType { get; init; }
    public object DataSource { get; init; }
    public string CollectionName { get; init; }



    public virtual async Task<InstanceCollection> InitializeAsync(CancellationToken cancellationToken)
    {
        var initialData = await InitializeDataAsync(cancellationToken);
        return new(){Instances = initialData.ToImmutableDictionary(GetKey, x => x), GetKey = GetKey};
    }

    private Task<IEnumerable<object>> InitializeDataAsync(CancellationToken cancellationToken) 
        => InitializationFunction(cancellationToken);

    public void Dispose()
    {
        workspaceSubscription?.Dispose();
    }
}