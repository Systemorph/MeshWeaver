using System.Reflection;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.DataPlugin;

/* TODO List: 
 *  a) move code DataPlugin to opensmc -- done
 *  b) create an immutable variant of the workspace
 *  c) make workspace methods fully sync
 *  d) offload saves & deletes to a different hub
 *  e) configure Ifrs Hubs
 */

public class DataPlugin : MessageHubPlugin<DataPlugin, Workspace>
{
    private DataPluginConfiguration DataConfiguration { get; set; } = new();

    public DataPlugin(IMessageHub hub, MessageHubConfiguration configuration,
                      Func<DataPluginConfiguration, DataPluginConfiguration> dataConfiguration) : base(hub)
    {
        Register(HandleGetRequest);              // This takes care of all Read (CRUD)
        Register(HandleUpdateAndDeleteRequest);  // This takes care of all Update and Delete (CRUD)

        DataConfiguration = dataConfiguration(DataConfiguration);
    }

    public override async Task StartAsync()  // This takes care of the Create (CRUD)
    {
        await base.StartAsync();

        foreach (var typeConfig in DataConfiguration.TypeConfigurations)
        {
            var items = await typeConfig.DoInitialize();
            State.Update(items);
        }
    }

    private static MethodInfo updateElementsMethod = ReflectionHelper.GetMethodGeneric<DataPlugin>(x => x.UpdateElements<object>(null, null));
    private static MethodInfo deleteElementsMethod = ReflectionHelper.GetMethodGeneric<DataPlugin>(x => x.DeleteElements<object>(null, null));

    private IMessageDelivery HandleUpdateAndDeleteRequest(IMessageDelivery request)
    {
        var type = request.Message.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(UpdateBatchRequest<>) || type.GetGenericTypeDefinition() == typeof(DeleteBatchRequest<>))
        {
            var elementType = type.GetGenericArguments().First();
            var typeConfig = DataConfiguration.TypeConfigurations.FirstOrDefault(x =>
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
        State.Update(items);        // update the state in memory (workspace)
        Hub.Post(new DataChanged(items));      // notify all subscribers that the data has changed
    }

    async Task DeleteElements<T>(IMessageDelivery<DeleteBatchRequest<T>> request, TypeConfiguration<T> config) where T : class
    {
        var items = request.Message.Elements;
        await config.Delete(items);
        State.Delete(items);
        Hub.Post(new DataDeleted(items));
    }

    private IMessageDelivery HandleGetRequest(IMessageDelivery request)
    {
        var type = request.Message.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(GetManyRequest<>))
        {
            var elementType = type.GetGenericArguments().First();
            var getElementsMethod = ReflectionHelper.GetMethodGeneric<DataPlugin>(x => x.GetElements<object>(null));
            return (IMessageDelivery)getElementsMethod.MakeGenericMethod(elementType).InvokeAsFunction(this, request);
        }
        return request;
    }

    private IMessageDelivery GetElements<T>(IMessageDelivery<GetManyRequest<T>> request) where T : class
    {
        var query = State.Query<T>();
        var message = request.Message;
        if (message.PageSize is not null)
            query = query.Skip(message.Page * message.PageSize.Value).Take(message.PageSize.Value);
        var queryResult = query.ToArray();
        Hub.Post(queryResult, o => o.ResponseFor(request));
        return request.Processed();
    }
}
