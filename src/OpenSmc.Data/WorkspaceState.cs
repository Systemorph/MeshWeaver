using System.Collections.Immutable;

namespace OpenSmc.Data;

public record WorkspaceState
{
    private ImmutableDictionary<Type, ImmutableDictionary<object, object>> Data { get; init; } =
        ImmutableDictionary<Type, ImmutableDictionary<object, object>>.Empty;

    public WorkspaceState Update(IEnumerable<object> items, DataConfiguration configuration)
    {
        var newData = Data;
        foreach (var g in items.GroupBy(item => item.GetType()))
        {
            if (!newData.TryGetValue(g.Key, out var itemsOfType))
            {
                newData = newData.Add(g.Key, itemsOfType = ImmutableDictionary<object, object>.Empty);
            }

            if (!configuration.TypeConfigurations.TryGetValue(g.Key, out var config))
                continue;
            foreach (var item in g)
            {
                var key = config.GetKey(item);
                itemsOfType = itemsOfType.SetItem(key, item);
            }

            newData = newData.SetItem(g.Key, itemsOfType);
        }

        return this with { Data = newData };
    }

    // think about message forwarding and trigger saving to DataSource
    // storage feed must be a Hub

    public WorkspaceState Delete(IEnumerable<object> items, DataConfiguration configuration)
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
            if (!configuration.TypeConfigurations.TryGetValue(g.Key, out var config))
                continue;

            itemsOfType = itemsOfType.RemoveRange(g.Select(config.GetKey));
            newData = newData.SetItem(g.Key, itemsOfType);
        }

        return this with { Data = newData };
    }

    public (IEnumerable<T> Items, int Count) GetItems<T>()
    {
        if (Data.TryGetValue(typeof(T), out var itemsOfType))
        {
            return (itemsOfType.Values.Cast<T>(), itemsOfType.Count);
        }

        return (Enumerable.Empty<T>(), 0);
    }

    // 1st hub -> DataHub (unique source of truth for data)
    // Upon save issue DataChanged(e.g new unique version of data) to Sender(ResponseFor) and to the Subscribers as well
    // Post to DataSourceHub (lambda)

    // 2nd hub as Child -> DataSourceHub (reflects the state which is in DataSource, lacks behind DataHub)
    // applies lambda expression and calls Update of DataSource

    // on Initialization Hub1 will send GetManyRequest to Hub2 and Hub2 wakes up and StartAsync
    // Hub2 uses InitializeAsync from and loads data from DB and returns result to Hub1
    // as soon as Hub 1 will receive callback from Hub 2 it will finish its startup
    // right after startup both hubs will be in Sync
}

