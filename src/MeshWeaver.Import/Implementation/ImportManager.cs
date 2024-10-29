using System.Text;
using MeshWeaver.Activities;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.DataStructures;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Import.Implementation;

public class ImportManager
{
    private record ImportAddress;
    public ImportConfiguration Configuration { get; }

    private readonly IMessageHub importHub; 
    public IWorkspace Workspace { get; }

    public ImportManager(IWorkspace  workspace)
    {
        Workspace = workspace;
        Configuration = workspace.Hub.Configuration.GetListOfLambdas().Aggregate(new ImportConfiguration(workspace), (c,l) => l.Invoke(c));
        importHub = workspace.Hub.GetHostedHub(
            new ImportAddress(), 
            config => 
                config.WithHandler<ImportRequest>((_,r,ct) => 
                    HandleImportRequestAsync(r,ct)));
    }

    public IMessageDelivery DeliverMessage(IMessageDelivery message)
    {
        importHub.DeliverMessage(message.ForwardTo(importHub.Address));
        return message.Forwarded();
    }

    private async Task<IMessageDelivery> HandleImportRequestAsync(
        IMessageDelivery<ImportRequest> request,
        CancellationToken cancellationToken
    )
    {

        var activity = new Activity(ActivityCategory.Import, Workspace.Hub);


        try
        {
            await ImportImpl(request.Message, activity, cancellationToken);
            await activity.Complete(log =>
            {
                Workspace.Hub.Post(new ImportResponse(Workspace.Hub.Version, log), o => o.ResponseFor(request));
            }, cancellationToken: cancellationToken);


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
            await activity.Complete(log => Workspace.Hub.Post(new ImportResponse(Workspace.Hub.Version, log), o => o.ResponseFor(request)), cancellationToken: cancellationToken);
        }

        return request.Processed();
    }


    private async Task ImportImpl(ImportRequest request, Activity activity, CancellationToken cancellationToken)
    {
        try

        {
            var imported = await ImportInstancesAsync(request, activity, cancellationToken);
            if (activity.HasErrors())
                return;

            Configuration.Workspace.RequestChange(DataChangeRequest.Update(imported, null), activity);
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

    public async Task<IReadOnlyCollection<object>> ImportInstancesAsync(
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
