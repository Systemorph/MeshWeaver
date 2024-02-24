using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Text.Json;
using Json.Patch;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Collections;
using OpenSmc.Messaging;
using OpenSmc.Reflection;
using OpenSmc.Serialization;

namespace OpenSmc.Data;

public interface ITypeSource
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Type ElementType { get; }
    string CollectionName { get; }
    object GetKey(object instance);
    ITypeSource WithKey(Func<object, object> key);
    IReadOnlyDictionary<object, object> GetData();
    void SynchronizeChange(CollectionChange change);
    void RequestChange(DataChangeRequest request);

}

public abstract record TypeSource<TTypeSource>(Type ElementType, string CollectionName, IMessageHub Hub) : ITypeSource
    where TTypeSource : TypeSource<TTypeSource>
{

    protected readonly ISerializationService SerializationService = Hub.ServiceProvider.GetRequiredService<ISerializationService>();
    protected ImmutableDictionary<object, object> CurrentState { get; set; }
    protected ImmutableDictionary<object, object> LastSavedState { get; set; }


    ITypeSource ITypeSource.WithKey(Func<object, object> key)
        => This with { Key = key };
    public virtual object GetKey(object instance)
        => Key(instance);
    protected Func<object, object> Key { get; init; } = GetKeyFunction(ElementType);
    private static Func<object, object> GetKeyFunction(Type elementType)
    {
        var keyProperty = elementType.GetProperties().SingleOrDefault(p => p.HasAttribute<KeyAttribute>());
        if (keyProperty == null)
            keyProperty = elementType.GetProperties().SingleOrDefault(x => x.Name.ToLowerInvariant() == "id");
        if (keyProperty == null)
            return null;
        var prm = Expression.Parameter(typeof(object));
        return Expression
            .Lambda<Func<object, object>>(Expression.Convert(Expression.Property(Expression.Convert(prm, elementType), keyProperty), typeof(object)), prm)
            .Compile();
    }

    public void SetData(IEnumerable<object> instances, bool snapshotMode = false)
    {
        foreach (var g in instances.GroupBy(i => CurrentState.ContainsKey(GetKey(i))))
        {
            if (g.Key)
                Update(g);
            else
                Add(g);
        }
    }

    public IReadOnlyDictionary<object, object> GetData()
        => CurrentState;

    
    protected TTypeSource This => (TTypeSource)this;

    public abstract void Update(IEnumerable<object> instances);


    public abstract void Add(IEnumerable<object> instances);

    public abstract void Delete(IEnumerable<object> instances);


    public void SynchronizeChange(CollectionChange change)
    {
        var json = change.Change.ToString();
        if (json == null)
            return;
        switch (change.Type)
        {
            case CollectionChangeType.Full:
                var node = JsonNode.Parse(json);
                LastSaved = (JsonArray)node;
                break;
            case CollectionChangeType.Patch:
                var patch = JsonSerializer.Deserialize<JsonPatch>(json);
                LastSaved = (JsonArray)patch.Apply(LastSaved).Result;
                break;
        }
        LastSavedState = CurrentState = ConvertToDictionary(LastSaved);
    }

    protected JsonArray LastSaved { get; set; }

    private ImmutableDictionary<object, object> ConvertToDictionary(JsonArray array)
    {
        return array
            .Select(DeserializeArrayElements)
            .Where(x => x.Key != null)
            .ToImmutableDictionary();
    }

    private KeyValuePair<object, object> DeserializeArrayElements(JsonNode node)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue("$id", out var id) || id == null)
            return default;

        return new KeyValuePair<object, object>(SerializationService.Deserialize(id.ToString()),
            SerializationService.Deserialize(node.ToString()));
    }


    public virtual Task<IReadOnlyDictionary<object, object>> GetAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyDictionary<object, object>>(null);
    }


    public void RequestChange(DataChangeRequest request)
    {
        CurrentState = request switch
        {
            UpdateDataRequest => CurrentState.SetItems(request.Elements.Select(e =>
                new KeyValuePair<object, object>(GetKey(e), e))),
            DeleteDataRequest => CurrentState.RemoveRange(request.Elements.Select(GetKey)),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request, null)
        };
    }




    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        LastSavedState = CurrentState = (await GetAsync(cancellationToken)).ToImmutableDictionary();
    }

}

public record TypeSourceWithType<T>(IMessageHub Hub) : TypeSourceWithType<T, TypeSourceWithType<T>>(Hub)
{
    protected override void Update(IEnumerable<T> instances) => UpdateAction.Invoke(instances);
    protected Action<IEnumerable<T>> UpdateAction { get; init; } = _ => { };

    public TypeSourceWithType<T> WithUpdate(Action<IEnumerable<T>> update) => This with { UpdateAction = update };
    protected Action<IEnumerable<T>> AddAction { get; init; } = _ => { };


    protected override void Add(IEnumerable<T> instances) => AddAction.Invoke(instances);

    public TypeSourceWithType<T> WithAdd(Action<IEnumerable<T>> add) => This with { AddAction = add };

    protected Action<IEnumerable<T>> DeleteAction { get; init; }
    protected override void Delete(IEnumerable<T> instances) => DeleteAction.Invoke(instances);


    public TypeSourceWithType<T> WithDelete(Action<IEnumerable<T>> delete) => This with { DeleteAction = delete };


    protected Func<CancellationToken, Task<IReadOnlyDictionary<object, object>>> GetAction { get; init; }
    public TypeSourceWithType<T> WithGet(Func<CancellationToken, Task<IReadOnlyDictionary<object,object>>> getAction) => This with { GetAction = getAction };

    public override Task<IReadOnlyDictionary<object, object>> GetAsync(CancellationToken cancellationToken)
        => GetAction?.Invoke(cancellationToken) ??
           Task.FromResult<IReadOnlyDictionary<object, object>>(new Dictionary<object, object>());
    public virtual TypeSourceWithType<T> WithInitialData(Func<CancellationToken,Task<IEnumerable<T>>> initialData)
    {
        return this with
        {
            GetAction = async c => (await initialData(c)).ToDictionary(x => Key(x), x => (object)x)
        };
    }

}

public abstract record TypeSourceWithType<T, TTypeSource>(IMessageHub Hub) : TypeSource<TTypeSource>(typeof(T), typeof(T).FullName, Hub)
where TTypeSource: TypeSourceWithType<T, TTypeSource>
{

    public TTypeSource WithKey(Func<T, object> key)
        => This with { Key = o => key.Invoke((T)o) };

    public TTypeSource WithCollectionName(string collectionName) =>
        This with { CollectionName = collectionName };

    public override void Add(IEnumerable<object> instances)
        => Add(instances.Cast<T>());



    public override void Update(IEnumerable<object> instances)
    => Update(instances.Cast<T>());


    public override void Delete(IEnumerable<object> instances)
        => Delete(instances.Cast<T>());



    protected virtual void Add(IEnumerable<T> instances)
        => CurrentState = CurrentState.SetItems(instances.Select(i => new KeyValuePair<object, object>(GetKey(i), i)));

    protected virtual void Update(IEnumerable<T> instances)
        => CurrentState = CurrentState.SetItems(instances.Select(i => new KeyValuePair<object, object>(GetKey(i), i)));

    protected virtual void Delete(IEnumerable<T> instances)
        => CurrentState = CurrentState.RemoveRange(instances.Select(i => GetKey(i)));










}


public record TypeSourceWithTypeWithDataStorage<T>(IMessageHub Hub, IDataStorage Storage)
    : TypeSourceWithType<T, TypeSourceWithTypeWithDataStorage<T>>(Hub)
    where T : class
{



    protected override void Add(IEnumerable<T> instances)
    {
        Storage.Add(instances);
    }

    protected override void Update(IEnumerable<T> instances)
    {
        Storage.Update(instances);
    }

    protected override void Delete(IEnumerable<T> instances)
    {
        Storage.Delete(instances);
    }

    public override async Task<IReadOnlyDictionary<object, object>> GetAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await Storage.StartTransactionAsync(cancellationToken);
        return (await Storage.Query<T>().ToArrayAsync(cancellationToken))
            .ToDictionary(GetKey, x => (object)x);
    }

}