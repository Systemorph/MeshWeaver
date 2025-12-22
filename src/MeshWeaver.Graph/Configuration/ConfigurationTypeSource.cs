using MeshWeaver.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// TypeSource for DataModel that syncs to IConfigurationStorageService.
/// Loads all DataModels on init, syncs adds/updates/deletes to storage.
/// </summary>
public record DataModelTypeSource : TypeSourceWithType<DataModel, DataModelTypeSource>
{
    private readonly IConfigurationStorageService _storage;
    private readonly ILogger? _logger;
    private InstanceCollection _lastSaved = new();

    public DataModelTypeSource(IWorkspace workspace, object dataSource, IConfigurationStorageService storage)
        : base(workspace, dataSource)
    {
        _storage = storage;
        _logger = workspace.Hub.ServiceProvider.GetService<ILogger<DataModelTypeSource>>();
    }

    protected override async Task<InstanceCollection> InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken ct)
    {
        // Load all DataModels from storage
        _logger?.LogWarning("DataModelTypeSource.InitializeAsync: STARTING");
        var dataModels = await _storage.LoadAllAsync<DataModel>(ct);
        _lastSaved = new InstanceCollection(dataModels.Cast<object>(), TypeDefinition.GetKey);
        _logger?.LogWarning("DataModelTypeSource.InitializeAsync: Loaded {Count} DataModels", dataModels.Count);
        return _lastSaved;
    }

    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
    {
        _logger?.LogWarning("DataModelTypeSource.UpdateImpl: CALLED with {Count} instances", instances.Instances.Count);

        // Detect adds (new items)
        var adds = instances.Instances
            .Where(x => !_lastSaved.Instances.ContainsKey(x.Key))
            .Select(x => (DataModel)x.Value)
            .ToArray();

        // Detect updates
        var updates = instances.Instances
            .Where(x => _lastSaved.Instances.TryGetValue(x.Key, out var existing)
                        && !existing.Equals(x.Value))
            .Select(x => (DataModel)x.Value)
            .ToArray();

        // Detect deletes
        var deletes = _lastSaved.Instances
            .Where(x => !instances.Instances.ContainsKey(x.Key))
            .Select(x => (DataModel)x.Value)
            .ToArray();

        _logger?.LogWarning("DataModelTypeSource.UpdateImpl: adds={Adds}, updates={Updates}, deletes={Deletes}",
            adds.Length, updates.Length, deletes.Length);

        // Sync to storage
        foreach (var item in adds.Concat(updates))
            _ = _storage.SaveAsync(item);

        foreach (var item in deletes)
            _ = _storage.DeleteAsync<DataModel>(item.Id);

        _lastSaved = instances;
        return instances;
    }
}

/// <summary>
/// TypeSource for LayoutAreaConfig that syncs to IConfigurationStorageService.
/// Loads all LayoutAreaConfigs on init, syncs adds/updates/deletes to storage.
/// </summary>
public record LayoutAreaConfigTypeSource : TypeSourceWithType<LayoutAreaConfig, LayoutAreaConfigTypeSource>
{
    private readonly IConfigurationStorageService _storage;
    private readonly ILogger? _logger;
    private InstanceCollection _lastSaved = new();

    public LayoutAreaConfigTypeSource(IWorkspace workspace, object dataSource, IConfigurationStorageService storage)
        : base(workspace, dataSource)
    {
        _storage = storage;
        _logger = workspace.Hub.ServiceProvider.GetService<ILogger<LayoutAreaConfigTypeSource>>();
    }

    protected override async Task<InstanceCollection> InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken ct)
    {
        // Load all LayoutAreaConfigs from storage
        var configs = await _storage.LoadAllAsync<LayoutAreaConfig>(ct);
        _lastSaved = new InstanceCollection(configs.Cast<object>(), TypeDefinition.GetKey);
        _logger?.LogDebug("LayoutAreaConfigTypeSource.InitializeAsync: Loaded {Count} LayoutAreaConfigs", configs.Count);
        return _lastSaved;
    }

    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
    {
        // Detect adds (new items)
        var adds = instances.Instances
            .Where(x => !_lastSaved.Instances.ContainsKey(x.Key))
            .Select(x => (LayoutAreaConfig)x.Value)
            .ToArray();

        // Detect updates
        var updates = instances.Instances
            .Where(x => _lastSaved.Instances.TryGetValue(x.Key, out var existing)
                        && !existing.Equals(x.Value))
            .Select(x => (LayoutAreaConfig)x.Value)
            .ToArray();

        // Detect deletes
        var deletes = _lastSaved.Instances
            .Where(x => !instances.Instances.ContainsKey(x.Key))
            .Select(x => (LayoutAreaConfig)x.Value)
            .ToArray();

        _logger?.LogDebug("LayoutAreaConfigTypeSource.UpdateImpl: adds={Adds}, updates={Updates}, deletes={Deletes}",
            adds.Length, updates.Length, deletes.Length);

        // Sync to storage
        foreach (var item in adds.Concat(updates))
            _ = _storage.SaveAsync(item);

        foreach (var item in deletes)
            _ = _storage.DeleteAsync<LayoutAreaConfig>(item.Id);

        _lastSaved = instances;
        return instances;
    }
}
