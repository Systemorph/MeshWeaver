using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.DataStructures;
using OpenSmc.Messaging;

namespace OpenSmc.Import;

public class ImportManager(ImportConfiguration configuration)
{
    public ImportConfiguration Configuration { get; } = configuration;
    private readonly IActivityService activityService =
        configuration.Hub.ServiceProvider.GetRequiredService<IActivityService>();

    public async Task<ActivityLog> ImportAsync(
        ImportRequest importRequest,
        CancellationToken cancellationToken
    )
    {
        activityService.Start();

        try
        {
            var (dataSet, format) = await ReadDataSetAsync(importRequest, cancellationToken);

            var hasError = format.Import(importRequest, dataSet);

            if (hasError)
                activityService.LogError(ValidationStageFailed);

            if (!activityService.HasErrors())
                Configuration.Workspace.Commit();
            else
                Configuration.Workspace.Rollback();

            if (format.SaveLog)
                Configuration.Workspace.Update(activityService.GetCurrentActivityLog());
            return activityService.Finish();

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
            return activityService.Finish();
        }
        finally
        {
            activityService.LogInformation($"Import finished.");
        }
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

    public static string ValidationStageFailed = "Validation stage has failed.";
}
