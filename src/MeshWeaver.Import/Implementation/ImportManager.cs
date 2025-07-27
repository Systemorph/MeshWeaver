using System.Text;
using MeshWeaver.Data;
using MeshWeaver.DataStructures;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Import.Implementation;

public class ImportManager
{
    private record ImportAddress() : Address("import", Guid.NewGuid().AsString());
    public ImportConfiguration Configuration { get; }

    private IMessageHub? importHub;
    private IMessageHub ImportHub
    {
        get
        {
            if (importHub == null)
            {
                var logger = Hub.ServiceProvider.GetService<ILogger<ImportManager>>();
                logger?.LogDebug("ImportManager lazy-initializing import hub for address {ImportAddress}", new ImportAddress());
                importHub = Hub.GetHostedHub(new ImportAddress());
                logger?.LogDebug("ImportManager import hub initialized: {ImportHub}", importHub?.Address);
            }
            return importHub!;
        }
    }
    public IWorkspace Workspace { get; }
    public IMessageHub Hub { get; }

    public ImportManager(IWorkspace workspace, IMessageHub hub)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<ImportManager>>();
        logger?.LogDebug("ImportManager constructor starting for hub {HubAddress}", hub.Address);

        Workspace = workspace;
        Hub = hub;
        Configuration = hub.Configuration.GetListOfLambdas().Aggregate(new ImportConfiguration(workspace), (c, l) => l.Invoke(c));

        // Don't initialize the import hub in constructor - do it lazily to avoid timing issues
        logger?.LogDebug("ImportManager constructor completed for hub {HubAddress}", hub.Address);
    }

    public IMessageDelivery HandleImportRequest(IMessageDelivery<ImportRequest> request)
    {
        // Create cancellation token with timeout if specified in the import request
        var cancellationTokenSource = request.Message.Timeout.HasValue
            ? new CancellationTokenSource(request.Message.Timeout.Value)
            : new CancellationTokenSource();

        ImportHub.InvokeAsync(ct =>
        {
            // Combine the provided cancellation token with our timeout token
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationTokenSource.Token);
            return DoImport(request, combined.Token);
        }, ex =>
        {
            FailImport(ex, request);
            return Task.CompletedTask;
        });

        return request.Processed();
    }

    private void FailImport(Exception exception, IMessageDelivery<ImportRequest> request)
    {
        var message = new StringBuilder(exception.Message);
        while (exception.InnerException != null)
        {
            message.AppendLine(exception.InnerException.Message);
            exception = exception.InnerException;
        }

        var activity = new Activity(ActivityCategory.Import, Hub);
        Hub.Post(new LogRequest(new LogMessage(message.ToString(), LogLevel.Error)),
            o => o.WithTarget(activity.Address));

        
        activity.Complete(ActivityStatus.Failed, log => Hub.Post(new ImportResponse(Hub.Version, log), o => o.ResponseFor(request)));
    }

    private async Task<IMessageDelivery> DoImport(
        IMessageDelivery<ImportRequest> request,
        CancellationToken cancellationToken
    )
    {

        var activity = new Activity(ActivityCategory.Import, Hub);
        var importId = Guid.NewGuid().ToString("N")[..8];
        activity.LogInformation($"Starting import {importId} for request {request.Message.GetType().Name}");

        try
        {
            await ImportImpl(request, activity, cancellationToken);
            activity.LogInformation("Import {ImportId} implementation completed, starting Activity.Complete", importId);

            activity.Complete(log =>
            {
                Hub.Post(new ImportResponse(Hub.Version, log), o => o.ResponseFor(request));
                activity.Dispose();
            });

            activity.LogInformation("Import {ImportId} Activity.Complete finished successfully", importId);
        }
        catch (Exception e)
        {
            activity.LogError("Import {ImportId} failed with exception: {Exception}", importId, e.Message);
            FinishWithException(request, e, activity);
        }

        return request.Processed();
    }

    private void FinishWithException(IMessageDelivery request, Exception e,
        Activity activity)
    {
        var message = new StringBuilder(e.Message);
        while (e.InnerException != null)
        {
            message.AppendLine(e.InnerException.Message);
            e = e.InnerException;
        }

        activity.LogError(message.ToString());
        activity.Complete(log => Hub.Post(new ImportResponse(Hub.Version, log), o => o.ResponseFor(request)));
    }


    private async Task ImportImpl(IMessageDelivery<ImportRequest> request, Activity activity, CancellationToken cancellationToken)
    {
        try

        {
            var imported = await ImportInstancesAsync(request.Message, cancellationToken);
            var log = await activity.GetLogAsync();
            if (log.HasErrors())
                return;

            Configuration.Workspace.RequestChange(
                DataChangeRequest.Update(
                    imported.Collections.Values.SelectMany(x => x.Instances.Values).ToArray(),
                    null,
                    request.Message.UpdateOptions),
                activity,
                request
                );
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
        }
    }

    public async Task<EntityStore> ImportInstancesAsync(
        ImportRequest importRequest,
        CancellationToken cancellationToken)
    {
        var (dataSet, format) = await ReadDataSetAsync(importRequest, cancellationToken);
        var imported = await format.Import(importRequest, dataSet);
        return imported!;
    }

    private async Task<(IDataSet dataSet, ImportFormat format)> ReadDataSetAsync(
        ImportRequest importRequest,
        CancellationToken cancellationToken
    )
    {
        var sourceType = importRequest.Source.GetType();
        if (!Configuration.StreamProviders.TryGetValue(sourceType, out var streamProvider))
            throw new ImportException($"Unknown stream type: {sourceType.FullName}");

        var stream = streamProvider.Invoke(importRequest);
        if (stream == null)
            throw new ImportException($"Could not open stream: {importRequest.Source}");

        if (!Configuration.DataSetReaders.TryGetValue(importRequest.MimeType, out var reader))
            throw new ImportException($"Cannot read mime type {importRequest.MimeType}");

        var dataSetReaderOptions = importRequest.DataSetReaderOptions;
        if (dataSetReaderOptions.EntityType == null)
            dataSetReaderOptions = dataSetReaderOptions with
            {
                EntityType = importRequest.EntityType
            };
        var (dataSet, format) = await reader.Invoke(
            stream,
            dataSetReaderOptions,
            cancellationToken
        );

        format ??= importRequest.Format;
        if (format == null)
            throw new ImportException("Format not specified.");

        var importFormat = Configuration.GetFormat(format);
        if (importFormat == null)
            throw new ImportException($"Unknown format: {format}");

        return (dataSet, importFormat);
    }

    public static string ImportFailed = "Import Failed. See Activity Log for Errors";
}
