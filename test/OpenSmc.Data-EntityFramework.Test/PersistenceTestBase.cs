using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using Xunit.Abstractions;

namespace OpenSmc.Data_EntityFramework.Test;

public class PersistenceTestBase : HubTestBase
{
    protected Task<IReadOnlyCollection<TEntity>> QueryAsync<TEntity>(object dataSourceId, CancellationToken cancellationToken = default) where TEntity : class
    {
        // this is code which emulates execution on server side, as we are working with physical services.
        // when doing this, make sure you are a plugin inside the clock of the server you are accessing.
        var host = GetHost();
        var workspace = host.ServiceProvider.GetRequiredService<IWorkspace>();
        var dataSource = workspace.Context.GetDataSource(dataSourceId);
        var storage = ((DataSourceWithStorage)dataSource).Storage;


        // this is usually not to be written ==> just test code.
        var persistenceHub = host.GetHostedHub(new DataPlugin.DataPersistenceAddress(host.Address), null);
        var messageService = persistenceHub.ServiceProvider.GetRequiredService<IMessageService>();
        var tcs = new TaskCompletionSource<IReadOnlyCollection<TEntity>>(cancellationToken);
        messageService.Schedule(async () =>
        {
            await using var transaction = await storage.StartTransactionAsync();
            try
            {
                var inStorage = await storage.Query<TEntity>().ToArrayAsync(cancellationToken);
                tcs.SetResult(inStorage);
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }

        });
        return tcs.Task;
    }

    protected PersistenceTestBase(ITestOutputHelper output) : base(output)
    {
    }
}