using System.Diagnostics;
using System.Text;
using AngleSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Import;

public class ImportPlugin : MessageHubPlugin, IMessageHandlerAsync<ImportRequest>
{
    private ImportManager importManager;
    private readonly IWorkspace workspace;
    private readonly IActivityService activityService;
    private readonly Func<ImportConfiguration, ImportConfiguration> importConfiguration;

    public ImportPlugin(
        IMessageHub hub,
        Func<ImportConfiguration, ImportConfiguration> importConfiguration
    )
        : base(hub)
    {
        workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();
        activityService = hub.ServiceProvider.GetRequiredService<IActivityService>();
        this.importConfiguration = importConfiguration;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
        initializeTask = InitializeAsync();
    }

    private Task initializeTask;
    public override Task Initialized => initializeTask;

    private async Task InitializeAsync()
    {
        await workspace.Initialized;
        importManager = new ImportManager(
            importConfiguration.Invoke(new(Hub, workspace.MappedTypes, activityService))
        );
    }

    public async Task<IMessageDelivery> HandleMessageAsync(
        IMessageDelivery<ImportRequest> request,
        CancellationToken cancellationToken
    )
    {
        activityService.Start();
        ActivityLog log;

        try
        {
            var (state, hasError) = await importManager.ImportAsync(
                request.Message,
                workspace.State,
                activityService,
                cancellationToken
            );

            if (hasError)
                activityService.LogError(ImportManager.ImportFailed);

            if (!activityService.HasErrors())
            {
                workspace.Update(
                    new ChangeItem<EntityStore>(
                        Hub.Address,
                        workspace.Reference,
                        state.Store,
                        Hub.Address,
                        Hub.Version
                    )
                );
            }

            log = activityService.Finish();

            if (request.Message.SaveLog)
            {
                workspace.Update(log);
            }

            //activityService.Finish();
        }
        catch (Exception e)
        {
            var message = new StringBuilder(e.Message);
            while (e.InnerException != null)
            {
                message.AppendLine(e.InnerException.Message);
                e = e.InnerException;
            }

            activityService.LogError(message.ToString());
            log = activityService.Finish();
        }

        Hub.Post(new ImportResponse(Hub.Version, log), o => o.ResponseFor(request));
        return request.Processed();
    }
}
