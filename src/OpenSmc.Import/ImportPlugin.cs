using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.DataStructures;
using OpenSmc.Messaging;
using OpenSmc.Reflection;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Import;

public class ImportPlugin : MessageHubPlugin<ImportState>,
    IMessageHandlerAsync<ImportRequest>
{
    [Inject] private IActivityService activityService;
    private readonly IWorkspace workspace;
    public ImportConfiguration Configuration;
    public ImportPlugin(IMessageHub hub, Func<ImportConfiguration, ImportConfiguration> importConfiguration) : base(hub)
    {
        workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();
        Configuration = importConfiguration.Invoke(new(workspace));
    }

    public override async Task StartAsync()
    {
        await base.StartAsync();
        await workspace.Initializing;
    }

    public async Task<IMessageDelivery> HandleMessageAsync(IMessageDelivery<ImportRequest> request, CancellationToken cancellationToken)
    {
        ActivityLog log;
        activityService.Start(); 

        try
        {
            var importRequest = request.Message;
            var (dataSet, format) = await ReadDataSetAsync(importRequest, cancellationToken);

            format.Import(importRequest, dataSet);

            if (!activityService.HasErrors())
                workspace.Commit();
            else
                workspace.Rollback();
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
        }
        finally
        {
            activityService.LogInformation($"Import finished.");
            Hub.Post(new DataChanged(Hub.Version) { Log = activityService.Finish() }, o => o.ResponseFor(request));
        }

        return request.Processed();
    }

    private IMessageDelivery FinishImport(IMessageDelivery<ImportRequest> request)
    {
        return request;
    }

    public async Task<(IDataSet dataSet, ImportFormat format)> ReadDataSetAsync(ImportRequest importRequest,
        CancellationToken cancellationToken)
    {
        if (!Configuration.StreamProviders.TryGetValue(importRequest.StreamType, out var streamProvider))
            throw new ImportException($"Unknown stream type: {importRequest.StreamType}");




        var stream = streamProvider.Invoke(importRequest);
        if (stream == null)
            throw new ImportException($"Could not open stream: {importRequest.StreamType}, {importRequest.Content}");



        if (!Configuration.DataSetReaders.TryGetValue(importRequest.MimeType, out var reader))
            throw new ImportException($"Cannot read mime type {importRequest.MimeType}");

        var (dataSet, format) = await reader.Invoke(stream, importRequest.DataSetReaderOptions, cancellationToken);

        format ??= importRequest.Format;
        if (format == null)
            throw new ImportException("Format not specified.");

        var importFormat = Configuration.GetFormat(format);
        if (importFormat == null)
            throw new ImportException($"Unknown format: {format}");

        return (dataSet, importFormat);
    }

    private IMessageDelivery Fail(string s)
    {
        throw new NotImplementedException();
    }
#pragma warning disable 4014
    private static readonly IGenericMethodCache UpdateMethod =
        GenericCaches.GetMethodCacheStatic(() => PerformUpdate<object>(null, null));
#pragma warning disable 4014


    private static void PerformUpdate<T>(IWorkspace targetDataSource, ICollection items) where T : class
    {
        var options = new UpdateOptions();

        targetDataSource.Update((items as IEnumerable<T>)?.ToArray());
    }

    public static string ValidationStageFailed = "Validation stage of type {0} has failed.";



    private ICollection CreateAndValidate<T>(IDataSet dataSet, IDataTable table,  ImportFormat format, Func<IDataSet, IDataRow, int, IEnumerable<T>> initFunc)
    {
        var hasError = true;
        var ret = new List<T>();

        for (var i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            if (row.ItemArray.Any(y => y != null))
            {
                foreach (var item in initFunc(dataSet, row, i) ?? Enumerable.Empty<T>())
                {
                    if (item == null)
                        continue;
                    foreach (var validation in format.Validations)
                        hasError = validation(item, new ValidationContext(item, Hub.ServiceProvider, State.ValidationCache)) && hasError;
                    ret.Add(item);
                }

            }
        }

        if (!hasError)
            activityService.LogError(string.Format(ValidationStageFailed, typeof(T).FullName));

        return ret;
    }

}