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
    private record ImportAddress() : Address("import", Guid.NewGuid().AsString());
    public ImportConfiguration Configuration { get; }

    private readonly IMessageHub importHub; 
    public IWorkspace Workspace { get; }
    public IMessageHub Hub { get; }

    public ImportManager(IWorkspace workspace, IMessageHub hub)
    {
        Workspace = workspace;
        Hub = hub;
        Configuration = hub.Configuration.GetListOfLambdas().Aggregate(new ImportConfiguration(workspace), (c,l) => l.Invoke(c));
        importHub = hub.GetHostedHub(new ImportAddress());
    }

    public IMessageDelivery HandleImportRequest(IMessageDelivery<ImportRequest> request)
    {
        importHub.InvokeAsync(ct => DoImport(request, ct), ex => FailImport(ex, request));
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
        activity.LogError(message.ToString());
        activity.Complete(log => Hub.Post(new ImportResponse(Hub.Version, log), o => o.ResponseFor(request)));
    }

    private async Task<IMessageDelivery> DoImport(
        IMessageDelivery<ImportRequest> request,
        CancellationToken cancellationToken
    )
    {

        var activity = new Activity(ActivityCategory.Import, Hub);


        try
        {
            await ImportImpl(request, activity, cancellationToken);
            await activity.Complete(log =>
            {
                Hub.Post(new ImportResponse(Hub.Version, log), o => o.ResponseFor(request));
            }, cancellationToken: cancellationToken);


        }
        catch (Exception e)
        {
            await FinishWithException(request,  e, activity);
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
        return imported;
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
