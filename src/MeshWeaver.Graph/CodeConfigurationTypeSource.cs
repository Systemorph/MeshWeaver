using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// TypeSource for CodeConfiguration that syncs to the NodeType's partition folder.
/// - On initialization: Loads CodeConfiguration from the partition
/// - On update: Syncs changes back to the partition via IPersistenceService
/// </summary>
public record CodeConfigurationTypeSource : TypeSourceWithType<CodeConfiguration, CodeConfigurationTypeSource>
{
    private readonly IPersistenceService _persistence;
    private readonly string _hubPath;
    private readonly IWorkspace _workspace;
    private readonly ILogger? _logger;
    private InstanceCollection _lastSaved = new();

    public CodeConfigurationTypeSource(IWorkspace workspace, object dataSource, IPersistenceService persistence, string hubPath)
        : base(workspace, dataSource)
    {
        _workspace = workspace;
        _persistence = persistence;
        _hubPath = hubPath;
        _logger = workspace.Hub.ServiceProvider.GetService<ILogger<CodeConfigurationTypeSource>>();
        _logger?.LogDebug("CodeConfigurationTypeSource: Created for hubPath={HubPath}", hubPath);

        // CodeConfiguration uses a fixed key since there's only one per NodeType
        TypeDefinition = workspace.Hub.TypeRegistry.WithKeyFunction(
            TypeDefinition.CollectionName,
            new KeyFunction(_ => "code", typeof(string)));
    }

    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
    {
        _logger?.LogDebug("CodeConfigurationTypeSource.UpdateImpl: Called with {Count} instances", instances.Instances.Count);

        // Detect changes
        var hasChanges = !_lastSaved.Instances.SequenceEqual(instances.Instances);

        if (hasChanges && instances.Instances.Count > 0)
        {
            var codeConfig = instances.Instances.Values.FirstOrDefault() as CodeConfiguration;
            if (codeConfig != null)
            {
                // Save to partition
                _logger?.LogDebug("CodeConfigurationTypeSource.UpdateImpl: Saving CodeConfiguration to partition {Path}", _hubPath);
                _ = _persistence.SavePartitionObjectsAsync(_hubPath, null, [codeConfig]);
            }
        }
        else if (hasChanges && instances.Instances.Count == 0 && _lastSaved.Instances.Count > 0)
        {
            // CodeConfiguration was deleted - we could handle this if needed
            _logger?.LogDebug("CodeConfigurationTypeSource.UpdateImpl: CodeConfiguration deleted from {Path}", _hubPath);
        }

        _lastSaved = instances;
        return instances;
    }

    protected override async Task<InstanceCollection> InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken ct)
    {
        _logger?.LogDebug("CodeConfigurationTypeSource.InitializeAsync: Loading from hubPath={HubPath}", _hubPath);

        // Load CodeConfiguration from the partition folder
        await foreach (var obj in _persistence.GetPartitionObjectsAsync(_hubPath, null))
        {
            if (obj is CodeConfiguration cc)
            {
                _logger?.LogDebug("CodeConfigurationTypeSource.InitializeAsync: Found CodeConfiguration");
                _lastSaved = new InstanceCollection([cc], TypeDefinition.GetKey);
                return _lastSaved;
            }
        }

        _logger?.LogDebug("CodeConfigurationTypeSource.InitializeAsync: No CodeConfiguration found");
        _lastSaved = new InstanceCollection();
        return _lastSaved;
    }
}
