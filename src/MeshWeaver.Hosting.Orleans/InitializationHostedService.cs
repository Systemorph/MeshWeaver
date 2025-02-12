using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Orleans;

public class InitializationHostedService(IMessageHub hub, IMeshCatalog catalog, ILogger<InitializationHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting initialization of {Address}", hub.Address);
        catalog.StartSync();
        return Task.CompletedTask;
    }

    public virtual Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
