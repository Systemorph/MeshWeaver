using System.Collections.Immutable;

namespace OpenSmc.Data;

public record Workspace(WorkspaceConfiguration Configuration)
{
    private ImmutableDictionary<Type, ImmutableDictionary<object, object>> data { get; init; } =
        ImmutableDictionary<Type, ImmutableDictionary<object, object>>.Empty;

    public Workspace Update(IEnumerable<object> items)
    {
        var newData = data;
        foreach (var g in items.GroupBy(item => item.GetType()))
        {
            if (!newData.TryGetValue(g.Key, out var itemsOfType))
            {
                newData = newData.Add(g.Key, itemsOfType = ImmutableDictionary<object, object>.Empty);
            }

            foreach (var item in g)
            {
                var key = Configuration.GetKey(item);
                itemsOfType = itemsOfType.SetItem(key, item);
            }

            newData = newData.SetItem(g.Key, itemsOfType);
        }

        return this with { data = newData };
    }

    // think about message forwarding and trigger saving to DataSource
    // storage feed must be a Hub

    public Workspace Delete(IEnumerable<object> items)
    {
        // TODO: this should create a copy of existed data, group by type, remove instances and return new clone with incremented version
        // RB: Not necessarily ==> data should be generally immutable

        var newData = data;
        foreach (var g in items.GroupBy(item => item.GetType()))
        {
            if (!newData.TryGetValue(g.Key, out var itemsOfType))
            {
                continue;
            }

            itemsOfType = itemsOfType.RemoveRange(g.Select(Configuration.GetKey));
            newData = newData.SetItem(g.Key, itemsOfType);
        }

        return this with { data = newData };
    }

    public (IEnumerable<T> Items, int Count) GetItems<T>()
    {
        if (data.TryGetValue(typeof(T), out var itemsOfType))
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

    // on Initialize Hub1 will send GetManyRequest to Hub2 and Hub2 wakes up and StartAsync
    // Hub2 uses InitializeAsync from and loads data from DB and returns result to Hub1
    // as soon as Hub 1 will receive callback from Hub 2 it will finish its startup
    // right after startup both hubs will be in Sync
}

public record WorkspaceConfiguration
{
    private ImmutableDictionary<Type, Func<object, object>> KeyFuncByType { get; init; } = ImmutableDictionary<Type, Func<object, object>>.Empty;
    public object GetKey(object item) => KeyFuncByType.TryGetValue(item.GetType(), out var func) ? func(item) : item; // TODO V10: Think about default case (02.02.2024, Alexander Yolokhov)

    public WorkspaceConfiguration Key<T>(Func<T, object> func)
    {
        return this with { KeyFuncByType = KeyFuncByType.SetItem(typeof(T), o => func((T)o)) };
    }
}