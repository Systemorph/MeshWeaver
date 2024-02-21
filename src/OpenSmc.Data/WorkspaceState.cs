using OpenSmc.Messaging;
using System.Collections.Immutable;

namespace OpenSmc.Data;


public record DataPluginState(CombinedWorkspaceState Current)
{
    public CombinedWorkspaceState PreviouslySaved { get; init; } = Current;
    public ImmutableList<DataChangeRequest> UncommittedEvents { get; init; } = ImmutableList<DataChangeRequest>.Empty;
}


public record CombinedWorkspaceState(ImmutableDictionary<object, WorkspaceState> WorkspacesByKey, DataContext Context) 
{
    public CombinedWorkspaceState Modify(IReadOnlyCollection<object> items, Func<WorkspaceState, IEnumerable<object>, WorkspaceState> modification)
    {
        var workspaces = WorkspacesByKey;

        foreach (var g in items.GroupBy(Context.GetDataSourceId))
        {
            var dataSourceId = g.Key;
            if(dataSourceId == null)
                continue;
            workspaces = workspaces.SetItem(dataSourceId, modification(GetWorkspace(dataSourceId), g));
        }

        return this with { WorkspacesByKey = workspaces };
    }

    public WorkspaceState GetWorkspace(object dataSourceId)
    {
        return WorkspacesByKey.GetValueOrDefault(dataSourceId) ?? new(Context.GetDataSource(dataSourceId));
    }

    public IReadOnlyCollection<T> GetItems<T>() where T : class
    {
        return WorkspacesByKey.Values.SelectMany(ws => ws.GetItems<T>()).ToArray();
    }
    public T GetItem<T>(object id) where T : class
    {
        return WorkspacesByKey.Values.Select(v => v.GetItem<T>(id)).FirstOrDefault(x => x != null);
    }

    public CombinedWorkspaceState UpdateWorkspace(object dataSourceId, WorkspaceState workspace)
        => this with { WorkspacesByKey = WorkspacesByKey.SetItem(dataSourceId, workspace) };
}

public record WorkspaceState(IDataSource DataSource)
{
    public long Version { get; init; }
    public ImmutableDictionary<string, ImmutableDictionary<object, object>> Data { get; init; } =
        ImmutableDictionary<string, ImmutableDictionary<object, object>>.Empty;

    public WorkspaceState SetData(Type type, ImmutableDictionary<object, object> instances)
        => DataSource.GetTypeConfiguration(type, out var typeConfig)
            ? SetData(type, typeConfig.CollectionName, instances)
            : throw new ArgumentException($"Type {type.FullName} has not been configured", nameof(type));

    public virtual WorkspaceState SetData(Type type, string collectionName, ImmutableDictionary<object, object> instances)
        => this with
        {
            Data = Data.SetItem(collectionName, instances),
            Version = typeof(IVersioned).IsAssignableFrom(type) ? instances.Values.OfType<IVersioned>().Max(v => v.Version) : Version
        };

    public virtual WorkspaceState Update(IEnumerable<object> items, bool snapshotModeEnabled)
    {
        var ret = this;

        foreach (var g in items.GroupBy(item => item.GetType()))
        {
            if (!DataSource.GetTypeConfiguration(g.Key, out var config))
                continue;
            
            ret = ret.Update(g.Key, ImmutableDictionary<object, object>.Empty
                .SetItems(g.Select(i => new KeyValuePair<object, object>(config.GetKey(i), i))), snapshotModeEnabled);
        }

        return ret;
    }

    private WorkspaceState Update(Type type, ImmutableDictionary<object, object> instances, bool snapshotModeEnabled)
        => SetData(type, (!snapshotModeEnabled ? GetValues(type) : ImmutableDictionary<object, object>.Empty ).SetItems(instances));

    private ImmutableDictionary<object, object> GetValues(Type type)
        => DataSource.GetTypeConfiguration(type, out var config)
            ? GetValues(config.CollectionName)
            : ThrowTypeNotConfigured(type);

    private static ImmutableDictionary<object, object> ThrowTypeNotConfigured(Type type)
    {
        throw new ArgumentException($"Type {type.FullName} has not been configured.", nameof(type));
    }

    private ImmutableDictionary<object,object> GetValues(string collection) 
        => Data.GetValueOrDefault(collection) ?? ImmutableDictionary<object, object>.Empty;

    // think about message forwarding and trigger saving to DataSource
    // storage feed must be a Hub

    public virtual WorkspaceState Delete(IEnumerable<object> items)
    {
        // TODO: this should create a copy of existed data, group by type, remove instances and return new clone with incremented version
        // RB: Not necessarily ==> data should be generally immutable

        var newData = Data;
        foreach (var g in items.GroupBy(item => item.GetType()))
        {
            if (!DataSource.GetTypeConfiguration(g.Key, out var typeConfiguration) || !newData.TryGetValue(typeConfiguration.CollectionName, out var itemsOfType))
                continue;

            itemsOfType = itemsOfType.RemoveRange(g.Select(typeConfiguration.GetKey));
            newData = newData.SetItem(typeConfiguration.CollectionName, itemsOfType);
        }

        return this with { Data = newData };
    }

    public virtual IReadOnlyCollection<T>  GetItems<T>()
    {
        return GetItemsByKey(typeof(T)).Values.Cast<T>().ToArray();
    }

    private ImmutableDictionary<object, object> GetItemsByKey(Type type)
    {
        if (!DataSource.GetTypeConfiguration(type, out var typeConfig))
            ThrowTypeNotConfigured(type);
        if (Data.TryGetValue(typeConfig.CollectionName, out var itemsOfType))
            return itemsOfType;
        return ImmutableDictionary<object, object>.Empty;
    }

    // 1st hub -> DataHub (unique source of truth for data)
    // Upon save issue DataChangedEvent(e.g new unique version of data) to Sender(ResponseFor) and to the Subscribers as well
    // Post to DataSourceHub (lambda)

    // 2nd hub as Child -> DataSourceHub (reflects the state which is in DataSource, lacks behind DataHub)
    // applies lambda expression and calls Modify of DataSource

    // on InitializeAction Hub1 will send GetManyRequest to Hub2 and Hub2 wakes up and StartAsync
    // Hub2 uses InitializeAsync from and loads data from DB and returns result to Hub1
    // as soon as Hub 1 will receive callback from Hub 2 it will finish its startup
    // right after startup both hubs will be in Sync
    public T GetItem<T>(object id) where T : class
    {
        return (T)GetItemsByKey(typeof(T)).GetValueOrDefault(id);
    }
}

