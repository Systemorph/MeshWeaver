using System.Text;
using Microsoft.Extensions.Logging;
using OpenSmc.Data;
using OpenSmc.DataStructures;
using OpenSmc.Import.Configuration;

namespace OpenSmc.Import.Implementation;

public class ImportManager(ImportConfiguration configuration)
{
    public ImportConfiguration Configuration { get; } = configuration;

    public async Task<(WorkspaceState State, bool Error)> ImportAsync(
        ImportRequest importRequest,
        WorkspaceState state,
        ILogger activityLog,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var (dataSet, format) = await ReadDataSetAsync(importRequest, cancellationToken);

            return format.Import(importRequest, dataSet, state);
        }
        catch (Exception e)
        {
            var message = new StringBuilder(e.Message);
            while (e.InnerException != null)
            {
                message.AppendLine(e.InnerException.Message);
                e = e.InnerException;
            }

            activityLog.LogError(message.ToString());
            return (state, true);
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

    public static string ImportFailed = "Import Failed. See Activity Log for Errors";
}
