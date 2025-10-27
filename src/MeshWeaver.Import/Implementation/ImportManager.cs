using System.Text;
using MeshWeaver.Data;
using MeshWeaver.DataStructures;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Import.Implementation;

public class ImportManager
{
    public ImportBuilder Configuration { get; }

    public IWorkspace Workspace { get; }
    public IMessageHub Hub { get; }

    public ImportManager(IWorkspace workspace, IMessageHub hub)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<ImportManager>>();
        logger?.LogDebug("ImportManager constructor starting for hub {HubAddress}", hub.Address);

        Workspace = workspace;
        Hub = hub;
        Configuration = hub.Configuration.GetListOfLambdas().Aggregate(new ImportBuilder(workspace, hub.ServiceProvider), (c, l) => l.Invoke(c));

        // Don't initialize the import hub in constructor - do it lazily to avoid timing issues
        logger?.LogDebug("ImportManager constructor completed for hub {HubAddress}", hub.Address);
    }

    public Task<IMessageDelivery> HandleImportRequest(IMessageDelivery<ImportRequest> request, CancellationToken cancellationToken)
    {
        // Create cancellation token with timeout if specified in the import request
        var cancellationTokenSource = request.Message.Timeout.HasValue
            ? new CancellationTokenSource(request.Message.Timeout.Value)
            : new CancellationTokenSource();

        // Combine the provided cancellation token with our timeout token
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancellationTokenSource.Token);
        return DoImport(request, combined.Token);
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

        activity.Complete(ActivityStatus.Failed, log => Hub.Post(new ImportResponse(Hub.Version, log), o => o.ResponseFor(request)));
    }

    private async Task<IMessageDelivery> DoImport(
        IMessageDelivery<ImportRequest> request,
        CancellationToken cancellationToken
    )
    {

        await ImportImpl(request, cancellationToken);
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


    private async Task ImportImpl(IMessageDelivery<ImportRequest> request, CancellationToken cancellationToken)
    {
        var activity = new Activity(ActivityCategory.Import, Hub, autoClose: false);
        var importActivity = activity.StartSubActivity(ActivityCategory.Import);
        try
        {

            activity.LogInformation("Starting import {ActivityId} for request {RequestId}", activity.Id, request.Id);

            var imported = await ImportInstancesAsync(request.Message, importActivity, cancellationToken);
            importActivity.Complete(log =>
            {
                if (log.HasErrors())
                {
                    Hub.Post(new ImportResponse(Hub.Version, log), o => o.ResponseFor(request));
                    return;
                }
                Configuration.Workspace.RequestChange(
                    DataChangeRequest.Update(
                        imported.Collections.Values.SelectMany(x => x.Instances.Values).ToArray(),
                        null,
                        request.Message.UpdateOptions),
                    activity,
                    request
                );
                activity.LogInformation("Finished import {ActivityId} for request {RequestId}", activity.Id, request.Id);
                Hub.Post(new ImportResponse(Hub.Version, log), o => o.ResponseFor(request));

            });

        }
        catch (Exception e)
        {
            importActivity.LogError(e.Message);
            importActivity.Complete();

            activity.LogError("Import {ImportId} for {RequestId} failed with exception: {Exception}", activity.Id, request.Id, e.Message);
            FinishWithException(request, e, activity);
        }
    }

    public async Task<EntityStore> ImportInstancesAsync(
        ImportRequest importRequest,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        // If ExcelImportConfiguration is provided, use ConfiguredExcelImporter directly
        if (importRequest.Configuration is ExcelImportConfiguration excelConfig)
        {
            return await ImportWithConfiguredExcelImporter(importRequest, excelConfig, activity, cancellationToken);
        }

        var (dataSet, format) = await ReadDataSetAsync(importRequest, activity, cancellationToken);
        var imported = await format.Import(importRequest, dataSet, activity, cancellationToken);
        return imported!;
    }

    private async Task<EntityStore> ImportWithConfiguredExcelImporter(
        ImportRequest importRequest,
        ExcelImportConfiguration config,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        activity?.LogInformation("Using ConfiguredExcelImporter with TypeName: {TypeName}", config.TypeName);

        // Get the stream provider
        var sourceType = importRequest.Source.GetType();
        if (!Configuration.StreamProviders.TryGetValue(sourceType, out var streamProvider))
            throw new ImportException($"Unknown stream type: {sourceType.FullName}");

        var stream = await streamProvider.Invoke(importRequest);
        if (stream == null)
            throw new ImportException($"Could not open stream: {importRequest.Source}");

        // Get the source name for tracking
        var sourceName = importRequest.Source switch
        {
            CollectionSource cs => cs.Path,
            _ => "unknown"
        };

        // Resolve the entity type from TypeName
        if (string.IsNullOrWhiteSpace(config.TypeName))
            throw new ImportException("TypeName is required in ExcelImportConfiguration");

        var entityType = ResolveType(config.TypeName);
        if (entityType == null)
            throw new ImportException($"Could not resolve type: {config.TypeName}");

        activity?.LogInformation("Resolved entity type: {EntityType}", entityType.FullName);

        // Create entity builder using AutoEntityBuilder.CreateBuilder<T>() generic method
        var builderGenericMethod = typeof(AutoEntityBuilder).GetMethods()
            .FirstOrDefault(m => m.Name == nameof(AutoEntityBuilder.CreateBuilder) && m.IsGenericMethod);
        if (builderGenericMethod == null)
            throw new ImportException("Could not find AutoEntityBuilder.CreateBuilder<T> method");

        var builderMethod = builderGenericMethod.MakeGenericMethod(entityType);
        var entityBuilder = builderMethod.Invoke(null, null);
        if (entityBuilder == null)
            throw new ImportException("Failed to create entity builder");

        // Create ConfiguredExcelImporter using reflection (since it's generic)
        var importerType = typeof(ConfiguredExcelImporter<>).MakeGenericType(entityType);
        var importer = Activator.CreateInstance(importerType, entityBuilder);
        if (importer == null)
            throw new ImportException($"Failed to create ConfiguredExcelImporter<{entityType.Name}>");

        // Call the Import method
        var importMethod = importerType.GetMethod(nameof(ConfiguredExcelImporter<object>.Import), new[] { typeof(Stream), typeof(string), typeof(ExcelImportConfiguration) });
        if (importMethod == null)
            throw new ImportException("Could not find Import method on ConfiguredExcelImporter");

        var importedEntities = importMethod.Invoke(importer, new object[] { stream, sourceName, config }) as System.Collections.IEnumerable;
        if (importedEntities == null)
            throw new ImportException("Import returned null");

        // Convert to EntityStore
        var entities = importedEntities.Cast<object>().ToArray();
        activity?.LogInformation("Imported {Count} entities of type {TypeName}", entities.Length, config.TypeName);

        var entityStore = new EntityStore();
        var instanceDict = new Dictionary<object, object>();

        foreach (var entity in entities)
        {
            var id = GetEntityId(entity, entityType);
            instanceDict[id] = entity;
        }

        var collection = new InstanceCollection(instanceDict);
        var collectionName = entityType.FullName ?? entityType.Name;

        entityStore = entityStore.WithCollection(collectionName, collection);
        return entityStore;
    }

    private string GetEntityId(object entity, Type entityType)
    {
        // Try to get Id property
        var idProp = entityType.GetProperty("Id");
        if (idProp != null)
        {
            var idValue = idProp.GetValue(entity);
            if (idValue != null)
                return idValue.ToString() ?? Guid.NewGuid().ToString();
        }

        // Fall back to hash code or GUID
        return entity.GetHashCode().ToString();
    }

    private Type? ResolveType(string typeName)
    {
        return Hub.TypeRegistry.GetType(typeName);
    }

    private async Task<(IDataSet dataSet, ImportFormat format)> ReadDataSetAsync(ImportRequest importRequest,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var sourceType = importRequest.Source.GetType();
        if (!Configuration.StreamProviders.TryGetValue(sourceType, out var streamProvider))
            throw new ImportException($"Unknown stream type: {sourceType.FullName}");

        var stream = await streamProvider.Invoke(importRequest);
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
        activity?.LogInformation("Read data set with {Tables} tables. Will import in format {Format}", dataSet.Tables.Count, format);

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
