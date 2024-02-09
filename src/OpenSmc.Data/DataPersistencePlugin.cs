using System.Reflection;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public record GetDataStateRequest(WorkspaceConfiguration WorkspaceConfiguration) : IRequest<WorkspaceState>;

public class DataPersistencePlugin : MessageHubPlugin,
    IMessageHandlerAsync<GetDataStateRequest>, 
    IMessageHandlerAsync<UpdateDataRequest>,
    IMessageHandlerAsync<DeleteDataRequest>
{
    private DataPersistenceConfiguration DataPersistenceConfiguration { get; set; }

    public DataPersistencePlugin(IMessageHub hub, Func<DataPersistenceConfiguration, DataPersistenceConfiguration> configure) : base(hub)
    {
        DataPersistenceConfiguration = configure(new DataPersistenceConfiguration(hub));
    }

    private static MethodInfo updateElementsMethod = ReflectionHelper.GetStaticMethodGeneric(() => UpdateElements<object>(null, null));
    private static MethodInfo deleteElementsMethod = ReflectionHelper.GetStaticMethodGeneric(() => DeleteElements<object>(null, null));

    Task<IMessageDelivery> IMessageHandlerAsync<UpdateDataRequest>.HandleMessageAsync(IMessageDelivery<UpdateDataRequest> request)
    {
        return HandleMessageAsync(request, request.Message.Elements, updateElementsMethod);
    }

    Task<IMessageDelivery> IMessageHandlerAsync<DeleteDataRequest>.HandleMessageAsync(IMessageDelivery<DeleteDataRequest> request)
    {
        return HandleMessageAsync(request, request.Message.Elements, deleteElementsMethod);
    }

    async Task<IMessageDelivery> HandleMessageAsync(IMessageDelivery request, IReadOnlyCollection<object> items, MethodInfo method)
    {
        foreach (var elementsByType in items.GroupBy(x => x.GetType()))
        {
            var typeConfig = DataPersistenceConfiguration.TypeConfigurations.FirstOrDefault(x =>
                    x.GetType().GetGenericArguments().First() == elementsByType.Key);  // TODO: check whether this works

            if (typeConfig is null) continue; // TODO: think about partial vs full update

            await method.MakeGenericMethod(elementsByType.Key).InvokeAsActionAsync(elementsByType, typeConfig);
        }

        //Hub.Post(new DataChanged(items));      // notify all subscribers that the data has changed

        return request.Processed();
    }

    private static Task UpdateElements<T>(IEnumerable<object> items, TypeConfiguration<T> config) where T : class => config.Save(items.Cast<T>());
    private static Task DeleteElements<T>(IEnumerable<object> items, TypeConfiguration<T> config) where T : class => config.Delete(items.Cast<T>());

    async Task<IMessageDelivery> IMessageHandlerAsync<GetDataStateRequest>.HandleMessageAsync(IMessageDelivery<GetDataStateRequest> request)
    {
        var workspace = new WorkspaceState(request.Message.WorkspaceConfiguration);
        foreach (var typeConfiguration in DataPersistenceConfiguration.TypeConfigurations)
        {
            var items = await typeConfiguration.DoInitialize();
            workspace = workspace.Update(items);
        }

        Hub.Post(workspace, o => o.ResponseFor(request));
        return request.Processed();
    }
}