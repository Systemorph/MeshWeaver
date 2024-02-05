using OpenSmc.Messaging;
using OpenSmc.Reflection;
using System.Reflection;

namespace OpenSmc.DataPlugin;

public record GetDataStateRequest(WorkspaceConfiguration WorkspaceConfiguration) : IRequest<Workspace>;

public record DataPersistenceAddress(object Host) : IHostedAddress;

public class DataPersistencePlugin : MessageHubPlugin<Workspace>,
    IMessageHandlerAsync<GetDataStateRequest>
{
    private DataPersistenceConfiguration DataPersistenceConfiguration { get; set; }

    public DataPersistencePlugin(IMessageHub hub, Func<DataPersistenceConfiguration, DataPersistenceConfiguration> configure) : base(hub)
    {
        Register(HandleUpdateAndDeleteRequest);  // This takes care of all Update and Delete (CRUD)

        DataPersistenceConfiguration = configure(new DataPersistenceConfiguration(hub));
    }

    private static MethodInfo updateElementsMethod = ReflectionHelper.GetMethodGeneric<DataPersistencePlugin>(x => x.UpdateElements<object>(null, null));
    private static MethodInfo deleteElementsMethod = ReflectionHelper.GetMethodGeneric<DataPersistencePlugin>(x => x.DeleteElements<object>(null, null));

    private IMessageDelivery HandleUpdateAndDeleteRequest(IMessageDelivery request)
    {
        var type = request.Message.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(UpdateBatchRequest<>) || type.GetGenericTypeDefinition() == typeof(DeleteBatchRequest<>))
        {
            var elementType = type.GetGenericArguments().First();
            var typeConfig = DataPersistenceConfiguration.TypeConfigurations.FirstOrDefault(x =>
                    x.GetType().GetGenericArguments().First() == elementType);  // TODO: check whether this works

            if (typeConfig is null) return request;

            if (type.GetGenericTypeDefinition() == typeof(UpdateBatchRequest<>))
            {
                updateElementsMethod.MakeGenericMethod(elementType).InvokeAsFunction(this, typeConfig);
            }
            else if (type.GetGenericTypeDefinition() == typeof(DeleteBatchRequest<>))
            {
                deleteElementsMethod.MakeGenericMethod(elementType).InvokeAsFunction(this, typeConfig);
            }
        }
        return request.Processed();
    }

    async Task UpdateElements<T>(IMessageDelivery<UpdateBatchRequest<T>> request, TypeConfiguration<T> config) where T : class
    {
        var items = request.Message.Elements;
        await config.Save(items);   // save to db
        Hub.Post(new DataChanged(items));      // notify all subscribers that the data has changed
    }

    async Task DeleteElements<T>(IMessageDelivery<DeleteBatchRequest<T>> request, TypeConfiguration<T> config) where T : class
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

    private IMessageDelivery GetElements<T>(IMessageDelivery<GetManyRequest<T>> request) where T : class
    {
        var query = State.GetItems<T>();
        var message = request.Message;
        if (message.PageSize is not null)
            query = query.Skip(message.Page * message.PageSize.Value).Take(message.PageSize.Value);
        var queryResult = query.ToArray();
        Hub.Post(queryResult, o => o.ResponseFor(request));
        return request.Processed();
    }
}