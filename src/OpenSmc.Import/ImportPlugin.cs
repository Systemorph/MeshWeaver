using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data;
using OpenSmc.Messaging;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Import;

public class ImportPlugin(
    IMessageHub hub,
    Func<ImportConfiguration, ImportConfiguration> importConfiguration
) : MessageHubPlugin(hub), IMessageHandlerAsync<ImportRequest>
{
    private readonly IWorkspace workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
    }

    public override Task Initialized => workspace.Initialized;

    public async Task<IMessageDelivery> HandleMessageAsync(
        IMessageDelivery<ImportRequest> request,
        CancellationToken cancellationToken
    )
    {
        var importManager = new ImportManager(
            importConfiguration.Invoke(new(Hub, workspace)).Build()
        );
        var log = await importManager.ImportAsync(request.Message, cancellationToken);
        Hub.Post(new ImportResponse(Hub.Version, log), o => o.ResponseFor(request));
        return request.Processed();
    }
}
