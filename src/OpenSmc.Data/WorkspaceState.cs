using OpenSmc.Messaging;
using System.Collections.Immutable;

namespace OpenSmc.Data;


public record DataPluginState(CombinedWorkspaceState Current)
{
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

    public CombinedWorkspaceState UpdateWorkspace(object dataSourceId, WorkspaceState workspace)
        => this with { WorkspacesByKey = WorkspacesByKey.SetItem(dataSourceId, workspace) };
}

public record WorkspaceState( DataSource DataSource)
{
    public long Version { get; init; }
    public ImmutableDictionary<Type, ImmutableDictionary<object, object>> Data { get; init; } =
        ImmutableDictionary<Type, ImmutableDictionary<object, object>>.Empty;

    public virtual WorkspaceState SetData(Type type, ImmutableDictionary<object, object> instances)
        => this with
        {
            Data = Data.SetItem(type, instances),
            Version = typeof(IVersioned).IsAssignableFrom(type) ? instances.Values.OfType<IVersioned>().Max(v => v.Version) : Version
        };

    public virtual WorkspaceState Update(IEnumerable<object> items)
    {
        var ret = this;

        foreach (var g in items.GroupBy(item => item.GetType()))
        {
            if (!DataSource.GetTypeConfiguration(g.Key, out var config))
                continue;
            
            ret = ret.Update(g.Key, ImmutableDictionary<object, object>.Empty
                .SetItems(g.Select(i => new KeyValuePair<object, object>(config.GetKey(i), i))));
        }

        return ret;
    }

    private WorkspaceState Update(Type type, ImmutableDictionary<object, object> instances)
        => SetData(type, GetValues(type).SetItems(instances));

    private ImmutableDictionary<object,object> GetValues(Type type) 
        => Data.GetValueOrDefault(type) ?? ImmutableDictionary<object, object>.Empty;

    // think about message forwarding and trigger saving to DataSource
    // storage feed must be a Hub

    public virtual WorkspaceState Delete(IEnumerable<object> items)
    {
        // TODO: this should create a copy of existed data, group by type, remove instances and return new clone with incremented version
        // RB: Not necessarily ==> data should be generally immutable

        var newData = Data;
        foreach (var g in items.GroupBy(item => item.GetType()))
        {
            if (!newData.TryGetValue(g.Key, out var itemsOfType))
            {
                continue;
            }
            if (!DataSource.GetTypeConfiguration(g.Key, out var config))
                continue;

            itemsOfType = itemsOfType.RemoveRange(g.Select(config.GetKey));
            newData = newData.SetItem(g.Key, itemsOfType);
        }

        return this with { Data = newData };
    }

    public virtual IReadOnlyCollection<T>  GetItems<T>()
    {
        if (Data.TryGetValue(typeof(T), out var itemsOfType))
        {
            return itemsOfType.Values.Cast<T>().ToArray();
        }

        return Array.Empty<T>();
    }

    // 1st hub -> DataHub (unique source of truth for data)
    // Upon save issue DataChanged(e.g new unique version of data) to Sender(ResponseFor) and to the Subscribers as well
    // Post to DataSourceHub (lambda)

    // 2nd hub as Child -> DataSourceHub (reflects the state which is in DataSource, lacks behind DataHub)
    // applies lambda expression and calls Modify of DataSource

    // on InitializeAction Hub1 will send GetManyRequest to Hub2 and Hub2 wakes up and StartAsync
    // Hub2 uses InitializeAsync from and loads data from DB and returns result to Hub1
    // as soon as Hub 1 will receive callback from Hub 2 it will finish its startup
    // right after startup both hubs will be in Sync
}

