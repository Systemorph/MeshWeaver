using System.Text;
using MeshWeaver.Activities;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.DataStructures;
using MeshWeaver.Import.Configuration;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Import.Implementation;

public class ImportManager(ImportConfiguration configuration)
{
    public ImportConfiguration Configuration { get; } = configuration;

    public async Task<Activity<ChangeItem<EntityStore>>> ImportAsync(
        ImportRequest importRequest,
        CancellationToken cancellationToken
    )
    {
        var activity = new Activity<ChangeItem<EntityStore>>(ActivityCategory.Import, Configuration.Logger);
        try
        {
            var (dataSet, format) = await ReadDataSetAsync(importRequest, cancellationToken);
            var reference = format.GetWorkspaceReference(dataSet);
            var stream = Configuration.Workspace.GetStreamFor(reference, Configuration.Workspace.Hub.Address);

            stream.Update(store =>
            {
                var ret = stream.ApplyChanges(format.Import(importRequest, dataSet, store, activity));
                activity.Finish(ret);
                return ret;
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
            activity.Finish(null);
        }
        return activity;
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
