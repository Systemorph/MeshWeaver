using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshWeaver.Activities;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
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
    private readonly Func<ImportConfiguration, ImportConfiguration> importConfiguration;

    private readonly IWorkspace workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();
    private readonly IActivityService activityService = hub.ServiceProvider.GetRequiredService<IActivityService>();
    public override async Task StartAsync(IMessageHub hub, CancellationToken cancellationToken)
    {
        await base.StartAsync(hub, cancellationToken);
        initializeTask = InitializeAsync();
    }

    private Task initializeTask;
    public override Task Initialized => initializeTask;

    private async Task InitializeAsync()
    {
        await workspace.Initialized;
        importManager = new ImportManager(
            importConfiguration.Invoke(new(workspace, workspace.MappedTypes, activityService))
        );
    }

    public async Task<IMessageDelivery> HandleMessageAsync(
        IMessageDelivery<ImportRequest> request,
        CancellationToken cancellationToken
    )
    {
        activityService.Start(ActivityCategory.Import);
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
                workspace.RequestChange(s => new ChangeItem<WorkspaceState>(
                    Hub.Address,
                    workspace.Reference,
                    s.Merge(state),
                    Hub.Address,
                    null,
                    Hub.Version
                ));
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
