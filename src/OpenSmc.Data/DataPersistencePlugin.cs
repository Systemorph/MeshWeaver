using System.Reflection;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public record GetDataStateRequest : IRequest<WorkspaceState>;

public class DataPersistencePlugin(IMessageHub hub) : MessageHubPlugin(hub),
    IMessageHandlerAsync<GetDataStateRequest>,
    IMessageHandlerAsync<UpdateDataRequest>,
    IMessageHandlerAsync<DeleteDataRequest>
{
    private DataConfiguration DataConfiguration { get; } = hub.Configuration.Get<DataConfiguration>() ?? new();

    private static readonly MethodInfo UpdateElementsMethod = ReflectionHelper.GetStaticMethodGeneric(() => UpdateElements<object>(null, null));
    private static readonly MethodInfo DeleteElementsMethod = ReflectionHelper.GetStaticMethodGeneric(() => DeleteElements<object>(null, null));

    Task<IMessageDelivery> IMessageHandlerAsync<UpdateDataRequest>.HandleMessageAsync(IMessageDelivery<UpdateDataRequest> request)
    {
        return HandleMessageAsync(request, request.Message.Elements, UpdateElementsMethod);
    }

    Task<IMessageDelivery> IMessageHandlerAsync<DeleteDataRequest>.HandleMessageAsync(IMessageDelivery<DeleteDataRequest> request)
    {
        return HandleMessageAsync(request, request.Message.Elements, DeleteElementsMethod);
    }

    async Task<IMessageDelivery> HandleMessageAsync(IMessageDelivery request, IReadOnlyCollection<object> items, MethodInfo method)
    {
        foreach (var elementsByType in items.GroupBy(x => x.GetType()))
        {
            if (!DataConfiguration.TypeConfigurations.TryGetValue(elementsByType.Key, out var typeConfig))
                continue;

            await method.MakeGenericMethod(elementsByType.Key).InvokeAsActionAsync(elementsByType, typeConfig);
        }

        //Hub.Post(new DataChanged(items));      // notify all subscribers that the data has changed

        return request.Processed();
    }

    // ReSharper disable once UnusedMethodReturnValue.Local
    private static Task UpdateElements<T>(IEnumerable<object> items, TypeConfiguration<T> config) where T : class => config.Save(items.Cast<T>());
    // ReSharper disable once UnusedMethodReturnValue.Local
    private static Task DeleteElements<T>(IEnumerable<object> items, TypeConfiguration<T> config) where T : class => config.Delete(items.Cast<T>());

    async Task<IMessageDelivery> IMessageHandlerAsync<GetDataStateRequest>.HandleMessageAsync(IMessageDelivery<GetDataStateRequest> request)
    {
        var workspace = new WorkspaceState();
        foreach (var typeConfiguration in DataConfiguration.TypeConfigurations.Values)
        {
            var items = await typeConfiguration.DoInitialize();
            workspace = workspace.Update(items, DataConfiguration);
        }

        Hub.Post(workspace, o => o.ResponseFor(request));
        return request.Processed();
    }
}