using System.Collections.Immutable;
using System.Reflection;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data.Persistence;

public record GetDataStateRequest : IRequest<CombinedWorkspaceState>;


public record UpdateDataStateRequest(IReadOnlyCollection<DataChangeRequest> Events);

public class DataPersistencePlugin(IMessageHub hub, DataConfiguration configuration) :
    MessageHubPlugin<CombinedWorkspaceState>(hub),
    IMessageHandler<GetDataStateRequest>,
    IMessageHandlerAsync<UpdateDataStateRequest>
{
    public DataConfiguration Configuration { get; } = configuration;

    public override bool IsDeferred(IMessageDelivery delivery) => 
        delivery.Message.GetType().Namespace == typeof(GetDataStateRequest).Namespace;

    public override async Task StartAsync()
    {
        await base.StartAsync();
        var loadedWorkspaces =
            (await Configuration.DataSources
                .Distinct()
                .ToAsyncEnumerable()
                .SelectAwait(async kvp =>
                    new KeyValuePair<object, WorkspaceState>(kvp.Key, await kvp.Value.DoInitialize()))
                .ToArrayAsync())
            .ToImmutableDictionary();
                

        InitializeState(new(loadedWorkspaces, Configuration));
    }

    IMessageDelivery IMessageHandler<GetDataStateRequest>.HandleMessage(IMessageDelivery<GetDataStateRequest> request)
    {


        Hub.Post(State, o => o.ResponseFor(request));



        return request.Processed();
    }

    public async Task<IMessageDelivery> HandleMessageAsync(IMessageDelivery<UpdateDataStateRequest> request)
    {

        foreach (var g in request.Message.Events
                     .SelectMany(ev => ev.Elements
                         .Select(instance => new
                         {
                             Event = ev,
                             Type = instance.GetType(),
                             DataSource = Configuration.GetDataSourceId(instance),
                             Instance = instance
                         }))
                     .GroupBy(x => x.DataSource))
        {
            var dataSourceId = g.Key;
            if (dataSourceId == null)
                continue;
            var dataSource = Configuration.GetDataSource(dataSourceId);
            var workspace = State.GetWorkspace(dataSource);

            await using var transaction = await dataSource.StartTransactionAsync();
            var events = g.GroupBy(x => x.Instance).Distinct().ToArray();
            foreach (var e in events)
            {
                var eventType = e.Key;
                foreach (var typeGroup in e.GroupBy(x => x.Type))
                {
                    var elementType = typeGroup.Key;
                    if (!dataSource.GetTypeConfiguration(elementType, out var typeConfig))
                        continue;
                    var toBeUpdated = typeGroup.Select(x => x.Instance).ToDictionary(typeConfig.GetKey);
                    workspace.Data.TryGetValue(elementType, out var existing);
                    switch (eventType)
                    {
                        case UpdateDataRequest:
                            workspace = Update(workspace, typeConfig, existing, toBeUpdated);
                            break;
                        case DeleteDataRequest:
                            workspace = Delete(workspace, typeConfig, existing, toBeUpdated);
                            break;
                    }
                }

            }
            await transaction.CommitAsync();
            UpdateState(s => s.UpdateWorkspace(dataSourceId, workspace));
        }
        return request.Processed();

    }

    private WorkspaceState Update(WorkspaceState workspace, TypeConfiguration typeConfig, ImmutableDictionary<object, object> existingInstances, IDictionary<object, object> toBeUpdatedInstances)
    {

        var grouped = toBeUpdatedInstances.GroupBy(e => existingInstances.ContainsKey(e.Key), e => e.Value).ToDictionary(x => x.Key, x => x.ToArray());

        var newInstances = grouped.GetValueOrDefault(false);
        if(newInstances?.Length > 0)
           DoAdd(typeConfig.ElementType, newInstances, typeConfig);
        var existing = grouped.GetValueOrDefault(true);
        if(existing?.Length > 0)
            DoUpdate(typeConfig.ElementType, existing, typeConfig);

        return workspace with
        {
            Data = workspace.Data.SetItem(typeConfig.ElementType, existingInstances.SetItems(toBeUpdatedInstances))
        };

    }

    private void DoAdd(Type type, IEnumerable<object> instances, TypeConfiguration typeConfig)
    {
        AddElementsMethod.MakeGenericMethod(type).InvokeAsAction(instances, typeConfig);
    }

    private void DoUpdate(Type type, IEnumerable<object> instances, TypeConfiguration typeConfig)
    {
        UpdateElementsMethod.MakeGenericMethod(type).InvokeAsAction(instances, typeConfig);
    }


    private WorkspaceState Delete(WorkspaceState workspace, TypeConfiguration typeConfig, ImmutableDictionary<object, object> existingInstances, IDictionary<object, object> toBeUpdatedInstances)
    {
        var toBeDeleted = toBeUpdatedInstances.Select(i => existingInstances.GetValueOrDefault(i.Key)).Where(x => x !=null).ToArray();
            DeleteElementsMethod.MakeGenericMethod(typeConfig.ElementType).InvokeAsAction(toBeDeleted, typeConfig);
            return workspace with
            {
                Data = workspace.Data.SetItem(typeConfig.ElementType, existingInstances.RemoveRange(toBeUpdatedInstances.Keys))
            };
    }


    private static readonly MethodInfo AddElementsMethod = ReflectionHelper.GetStaticMethodGeneric(() => AddElements<object>(null, null));
    // ReSharper disable once UnusedMethodReturnValue.Local
    private static void AddElements<T>(IEnumerable<object> items, TypeConfiguration<T> config) where T : class
        => config.Add(items.Cast<T>().ToArray());


    private static readonly MethodInfo UpdateElementsMethod = ReflectionHelper.GetStaticMethodGeneric(() => UpdateElements<object>(null, null));
    // ReSharper disable once UnusedMethodReturnValue.Local
    private static void UpdateElements<T>(IEnumerable<object> items, TypeConfiguration<T> config) where T : class
    => config.Update(items.Cast<T>().ToArray());

    private static readonly MethodInfo DeleteElementsMethod = ReflectionHelper.GetStaticMethodGeneric(() => DeleteElements<object>(null, null));
    // ReSharper disable once UnusedMethodReturnValue.Local
    private static void DeleteElements<T>(IEnumerable<object> items, TypeConfiguration<T> config) where T : class => config.Delete(items.Cast<T>().ToArray());

}

internal class PersistenceException(string message) : Exception(message);

