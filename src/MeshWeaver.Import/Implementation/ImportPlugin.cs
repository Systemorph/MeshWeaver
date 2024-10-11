using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshWeaver.Data;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Messaging;

namespace MeshWeaver.Import.Implementation;

public static class ActivityCategory
{
    public const string Import = nameof(Import);
}

public class ImportPlugin(IMessageHub hub, Func<ImportConfiguration, ImportConfiguration> importConfiguration) : MessageHubPlugin(hub), IMessageHandlerAsync<ImportRequest>
{
    private ImportManager importManager;
    private ILogger logger = hub.ServiceProvider.GetRequiredService<ILogger<ImportPlugin>>();
    private readonly IWorkspace workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();
    public override async Task StartAsync(IMessageHub hub, CancellationToken cancellationToken)
    {
        await base.StartAsync(hub, cancellationToken);
        initializeTask = InitializeAsync();
    }

    private Task initializeTask;
    public override Task Initialized => initializeTask;

    private async Task InitializeAsync()
    {
        importManager = new ImportManager(
            importConfiguration.Invoke(new(workspace, workspace.MappedTypes, logger))
        );
    }

    public async Task<IMessageDelivery> HandleMessageAsync(
        IMessageDelivery<ImportRequest> request,
        CancellationToken cancellationToken
    )
    {

        var activity = await importManager.ImportAsync(
            request.Message,
            cancellationToken
        );

        try
        {

            activity.OnCompleted((_, log) =>
            {
                Hub.Post(new ImportResponse(Hub.Version, log), o => o.ResponseFor(request));
            });


        }
        catch (Exception e)
        {
            var message = new StringBuilder(e.Message);
            while (e.InnerException != null)
            {
                message.AppendLine(e.InnerException.Message);
                e = e.InnerException;
            }

            activity.LogError(message.ToString());
            var (_,log) = activity.Finish(null);
            Hub.Post(new ImportResponse(Hub.Version, log), o => o.ResponseFor(request));
        }

        return request.Processed();
    }
}
