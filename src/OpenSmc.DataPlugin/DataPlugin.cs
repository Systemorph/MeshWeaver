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

public record Workspace()
{
    public Workspace Update(IEnumerable<object> items) => this; // TODO: this should group by type and put to immutable Dictionary structure
    // think about message forwarding and trigger saving to DataSource
    // storage feed must be a Hub

    public Workspace Delete(IEnumerable<object> items) => this; // TODO: this should create a copy of existed data, group by type, remove instances and return new clone with incremented version

    public IQueryable<T> Query<T>() => new List<T>().AsQueryable();

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

    private IMessageDelivery HandleUpdateAndDeleteRequest(IMessageDelivery request)
    {
        var type = request.Message.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(UpdateBatchRequest<>) || type.GetGenericTypeDefinition() == typeof(DeleteBatchRequest<>))
        {
            var elementType = type.GetGenericArguments().First();
            var typeConfig = DataConfiguration.TypeConfigurations.FirstOrDefault(x =>
                    x.GetType().GetGenericArguments().First() == elementType);  // TODO: check whether this works

            if (typeConfig is null) return request;
            var config = (TypeConfiguration<object>)typeConfig;

            if (type.GetGenericTypeDefinition() == typeof(UpdateBatchRequest<>))
            {
                var getElementsMethod = ReflectionHelper.GetMethodGeneric<DataPlugin>(x => x.UpdateElements<object>(null, null));
                getElementsMethod.MakeGenericMethod(elementType).InvokeAsFunction(this, config.Save);
            }
            else if (type.GetGenericTypeDefinition() == typeof(DeleteBatchRequest<>))
            {
                var getElementsMethod = ReflectionHelper.GetMethodGeneric<DataPlugin>(x => x.DeleteElements<object>(null, null));
                getElementsMethod.MakeGenericMethod(elementType).InvokeAsFunction(this, config.Delete);
            }
        }
        return request.Processed();
    }

    async Task UpdateElements<T>(IMessageDelivery<UpdateBatchRequest<T>> request, Func<IReadOnlyCollection<T>, Task> save) where T : class
    {
        var items = request.Message.Elements;
        await save(items);                     // save to db
        State.Update(items);        // update the state in memory (workspace)
        Hub.Post(new DataChanged(items));      // notify all subscribers that the data has changed
    }

    async Task DeleteElements<T>(IMessageDelivery<DeleteBatchRequest<T>> request, Func<IReadOnlyCollection<T>, Task> delete) where T : class
    {
        var items = request.Message.Elements;
        await delete(items);
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
