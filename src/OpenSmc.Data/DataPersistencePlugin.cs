using System.Reflection;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.Data;

public record GetDataStateRequest : IRequest<WorkspaceState>;

public class DataPersistencePlugin(IMessageHub hub, DataConfiguration dataConfiguration) : 
    MessageHubPlugin(hub),
    IMessageHandlerAsync<GetDataStateRequest>,
    IMessageHandlerAsync<UpdateDataStateRequest>
{
    public DataConfiguration DataConfiguration { get; } = dataConfiguration;

    public override bool IsDeferred(IMessageDelivery delivery) => delivery.Message.GetType().Namespace == typeof(GetDataStateRequest).Namespace;

    private static readonly MethodInfo UpdateElementsMethod = ReflectionHelper.GetStaticMethodGeneric(() => UpdateElements<object>(null, null));
    private static readonly MethodInfo DeleteElementsMethod = ReflectionHelper.GetStaticMethodGeneric(() => DeleteElements<object>(null, null));

    Task HandleMessageAsync(UpdateDataRequest updateData)
    {
        return HandleMessageAsync(updateData.Elements, UpdateElementsMethod);
    }

    Task HandleMessageAsync(DeleteDataRequest deleteData)
    {
        return DeleteAsync(deleteData.Elements);
    }

    async Task HandleMessageAsync(IReadOnlyCollection<object> items, MethodInfo method)
    {
        foreach (var elementsByType in items.GroupBy(x => x.GetType()))
        {
            if (!DataConfiguration.TypeConfigurations.TryGetValue(elementsByType.Key, out var typeConfig))
                continue;

            await method.MakeGenericMethod(elementsByType.Key).InvokeAsActionAsync(elementsByType, typeConfig);
        }

        //Hub.Post(new DataChanged(items));      // notify all subscribers that the data has changed

    }

    private async Task DeleteAsync( IReadOnlyCollection<object> items)
    {
        foreach (var elementsByType in items.GroupBy(x => x.GetType()))
        {
            if (!DataConfiguration.TypeConfigurations.TryGetValue(elementsByType.Key, out var typeConfig))
                continue;

            //if (typeConfig.DeleteByIds != null)
            await DeleteElementsMethod.MakeGenericMethod(elementsByType.Key).InvokeAsActionAsync(elementsByType, typeConfig);
        }

    }


    // ReSharper disable once UnusedMethodReturnValue.Local
    private static Task UpdateElements<T>(IEnumerable<object> items, TypeConfiguration<T> config) where T : class => config.Save(items.Cast<T>());
    // ReSharper disable once UnusedMethodReturnValue.Local
    private static Task DeleteElements<T>(IEnumerable<object> items, TypeConfiguration<T> config) where T : class => config.Delete(items.Cast<T>());

    async Task<IMessageDelivery> IMessageHandlerAsync<GetDataStateRequest>.HandleMessageAsync(IMessageDelivery<GetDataStateRequest> request)
    {
        var workspace = new WorkspaceState(Hub.Version);
        foreach (var typeConfiguration in DataConfiguration.TypeConfigurations.Values)
        {
            var items = await typeConfiguration.DoInitialize();
            workspace = workspace.Update(items, DataConfiguration);
        }

        Hub.Post(workspace, o => o.ResponseFor(request));
        return request.Processed();
    }

    public async Task<IMessageDelivery> HandleMessageAsync(IMessageDelivery<UpdateDataStateRequest> request)
    {
        foreach (var dataChangeRequest in request.Message.Events)
        {
            await HandleMessageAsync((dynamic)dataChangeRequest);
        }

        return request.Processed();
    }
}