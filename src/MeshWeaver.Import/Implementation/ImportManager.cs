using System.Text;
using MeshWeaver.Activities;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.DataStructures;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Import.Implementation;

public class ImportManager
{
    private record ImportAddress() : Address("import", Guid.NewGuid().AsString()!);
    public ImportConfiguration Configuration { get; }

    private readonly IMessageHub importHub = null!;
    public IWorkspace Workspace { get; }
    public IMessageHub Hub { get; }

    public ImportManager(IWorkspace workspace, IMessageHub hub)
    {
        Workspace = workspace;
        Hub = hub;
        Configuration = hub.Configuration.GetListOfLambdas().Aggregate(new ImportConfiguration(workspace), (c, l) => l.Invoke(c));
        importHub = hub.GetHostedHub(new ImportAddress())!;
    }

    public IMessageDelivery HandleImportRequest(IMessageDelivery<ImportRequest> request)
    {
        // Create cancellation token with timeout if specified in the import request
        var cancellationTokenSource = request.Message.Timeout.HasValue
            ? new CancellationTokenSource(request.Message.Timeout.Value)
            : new CancellationTokenSource();

        importHub.InvokeAsync(ct =>
        {
            // Combine the provided cancellation token with our timeout token
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationTokenSource.Token);
            return DoImport(request, combined.Token);
        }, ex => FailImport(ex, request));

        return request.Processed();
    }

    private Task FailImport(Exception exception, IMessageDelivery<ImportRequest> request)
    {
        var message = new StringBuilder(exception.Message);
        while (exception.InnerException != null)
        {
            message.AppendLine(exception.InnerException.Message);
            exception = exception.InnerException;
        }
        var activity = new Activity(ActivityCategory.Import, Hub);
        activity.LogError(message.ToString());
        return activity.Complete(log => Hub.Post(new ImportResponse(Hub.Version, log), o => o.ResponseFor(request)));
    }

    private async Task<IMessageDelivery> DoImport(
        IMessageDelivery<ImportRequest> request,
        CancellationToken cancellationToken
    )
    {

        var activity = new Activity(ActivityCategory.Import, Hub);
        var importId = Guid.NewGuid().ToString("N")[..8];
        activity.LogInformation("Starting import {ImportId} for request {RequestType}", importId, request.Message.GetType().Name);

        try
        {
            await ImportImpl(request, activity, cancellationToken);
            activity.LogInformation("Import {ImportId} implementation completed, starting Activity.Complete", importId);

            await activity.Complete(log =>
            {
                activity.LogInformation("Import {ImportId} Activity.Complete callback executing", importId);
                Hub.Post(new ImportResponse(Hub.Version, log), o => o.ResponseFor(request));
                activity.LogInformation("Import {ImportId} response posted", importId);
            }, cancellationToken: cancellationToken);

            activity.LogInformation("Import {ImportId} Activity.Complete finished successfully", importId);
        }
        catch (Exception e)
        {
            activity.LogError("Import {ImportId} failed with exception: {Exception}", importId, e.Message);
            await FinishWithException(request, e, activity);
        }

        return request.Processed();
    }

    private async Task FinishWithException(IMessageDelivery request, Exception e,
        Activity activity)
    {
        var message = new StringBuilder(e.Message);
        while (e.InnerException != null)
        {
            message.AppendLine(e.InnerException.Message);
            e = e.InnerException;
        }

        activity.LogError(message.ToString());
        await activity.Complete(log => Hub.Post(new ImportResponse(Hub.Version, log), o => o.ResponseFor(request)));
    }


    private async Task ImportImpl(IMessageDelivery<ImportRequest> request, Activity activity, CancellationToken cancellationToken)
    {
        try

        {
            var imported = await ImportInstancesAsync(request.Message, activity, cancellationToken);
            if (activity.HasErrors())
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
        Activity activity,
        CancellationToken cancellationToken)
    {
        var (dataSet, format) = await ReadDataSetAsync(importRequest, cancellationToken);
        var imported = await format.Import(importRequest, dataSet, activity);
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

        return (dataSet, importFormat!);
    }

    public static string ImportFailed = "Import Failed. See Activity Log for Errors";
}
