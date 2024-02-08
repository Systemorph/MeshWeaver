using System.Reflection;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public record GetDataStateRequest(WorkspaceConfiguration WorkspaceConfiguration) : IRequest<Workspace>;

public class DataPersistencePlugin : MessageHubPlugin,
    IMessageHandlerAsync<GetDataStateRequest>, 
    IMessageHandlerAsync<UpdateDataRequest>
{
    private DataPersistenceConfiguration DataPersistenceConfiguration { get; set; }

    public DataPersistencePlugin(IMessageHub hub, Func<DataPersistenceConfiguration, DataPersistenceConfiguration> configure) : base(hub)
    {
        DataPersistenceConfiguration = configure(new DataPersistenceConfiguration(hub));
    }

    private static MethodInfo updateElementsMethod = ReflectionHelper.GetStaticMethodGeneric(() => UpdateElements<object>(null, null));
    private static MethodInfo deleteElementsMethod = ReflectionHelper.GetMethodGeneric<DataPersistencePlugin>(x => x.DeleteElements<object>(null, null));

    async Task<IMessageDelivery> IMessageHandlerAsync<UpdateDataRequest>.HandleMessageAsync(IMessageDelivery<UpdateDataRequest> request)
    {
        foreach (var elementsByType in request.Message.Elements.GroupBy(x => x.GetType()))
        {
            var typeConfig = DataPersistenceConfiguration.TypeConfigurations.FirstOrDefault(x =>
                    x.GetType().GetGenericArguments().First() == elementsByType.Key);  // TODO: check whether this works

            if (typeConfig is null) continue; // TODO: think about partial vs full update

            await updateElementsMethod.MakeGenericMethod(elementsByType.Key).InvokeAsActionAsync(elementsByType, typeConfig);
        }

        //Hub.Post(new DataChanged(items));      // notify all subscribers that the data has changed

        return request.Processed();
    }


    private static Task UpdateElements<T>(IEnumerable<object> items, TypeConfiguration<T> config) where T : class => config.Save(items.Cast<T>());   // save to db

    private async Task DeleteElements<T>(IMessageDelivery<DeleteBatchRequest<T>> request, TypeConfiguration<T> config) where T : class
    {
        var items = request.Message.Elements;
        await config.Delete(items);
        Hub.Post(new DataDeleted(items));
    }

    async Task<IMessageDelivery> IMessageHandlerAsync<GetDataStateRequest>.HandleMessageAsync(IMessageDelivery<GetDataStateRequest> request)
    {
        var workspace = new Workspace(request.Message.WorkspaceConfiguration);
        foreach (var typeConfiguration in DataPersistenceConfiguration.TypeConfigurations)
        {
            var items = await typeConfiguration.DoInitialize();
            workspace = workspace.Update(items);
        }

        Hub.Post(workspace, o => o.ResponseFor(request));
        return request.Processed();
    }
}