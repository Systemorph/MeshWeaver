﻿using OpenSmc.Messaging;
using OpenSmc.Reflection;
using System.Reflection;

namespace OpenSmc.Data;

public record GetDataStateRequest(WorkspaceConfiguration WorkspaceConfiguration) : IRequest<Workspace>;

public class DataPersistencePlugin : MessageHubPlugin,
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
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(UpdatePersistenceRequest<>) || type.GetGenericTypeDefinition() == typeof(DeleteBatchRequest<>)))
        {
            var elementType = type.GetGenericArguments().First();
            var typeConfig = DataPersistenceConfiguration.TypeConfigurations.FirstOrDefault(x =>
                    x.GetType().GetGenericArguments().First() == elementType);  // TODO: check whether this works

            if (typeConfig is null) return request;

            if (type.GetGenericTypeDefinition() == typeof(UpdatePersistenceRequest<>))
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

    async Task UpdateElements<T>(IMessageDelivery<UpdatePersistenceRequest<T>> request, TypeConfiguration<T> config) where T : class
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
}