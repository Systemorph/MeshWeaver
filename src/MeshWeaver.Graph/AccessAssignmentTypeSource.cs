using MeshWeaver.Data;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// TypeSource for AccessAssignment objects backed by ISecurityService.
/// - On initialization: Loads local assignments via GetAccessAssignmentsAsync (IsLocal == true)
/// - On update: Diffs against last saved state and calls Add/Remove/Toggle on ISecurityService
/// </summary>
public record AccessAssignmentTypeSource : TypeSourceWithType<AccessAssignment, AccessAssignmentTypeSource>
{
    private readonly ISecurityService _securityService;
    private readonly string _hubPath;
    private readonly ILogger? _logger;
    private InstanceCollection _lastSaved = new();

    public AccessAssignmentTypeSource(
        IWorkspace workspace,
        object dataSource,
        ISecurityService securityService,
        string hubPath)
        : base(workspace, dataSource)
    {
        _securityService = securityService;
        _hubPath = hubPath;
        _logger = workspace.Hub.ServiceProvider.GetService<ILogger<AccessAssignmentTypeSource>>();
    }

    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
    {
        // Detect adds (new assignments)
        var adds = instances.Instances
            .Where(x => !_lastSaved.Instances.ContainsKey(x.Key))
            .Select(x => (AccessAssignment)x.Value)
            .ToArray();

        // Detect updates (Denied toggled)
        var updates = instances.Instances
            .Where(x => _lastSaved.Instances.TryGetValue(x.Key, out var existing)
                        && !existing.Equals(x.Value))
            .Select(x => (AccessAssignment)x.Value)
            .ToArray();

        // Detect deletes
        var deletes = _lastSaved.Instances
            .Where(x => !instances.Instances.ContainsKey(x.Key))
            .Select(x => (AccessAssignment)x.Value)
            .ToArray();

        _logger?.LogDebug(
            "AccessAssignmentTypeSource.UpdateImpl: adds={Adds}, updates={Updates}, deletes={Deletes}",
            adds.Length, updates.Length, deletes.Length);

        var tasks = adds.Select(a => _securityService.AddUserRoleAsync(a.UserId, a.RoleId, _hubPath))
            .Concat(deletes.Select(a => _securityService.RemoveUserRoleAsync(a.UserId, a.RoleId, _hubPath)))
            .Concat(updates.Select(a => _securityService.ToggleRoleAssignmentAsync(_hubPath, a.UserId, a.RoleId, a.Denied)));

        _ = Task.WhenAll(tasks).ContinueWith(t =>
            _logger?.LogError(t.Exception, "Failed to sync access assignments for {HubPath}", _hubPath),
            TaskContinuationOptions.OnlyOnFaulted);

        _lastSaved = instances;
        return instances;
    }

    protected override async Task<InstanceCollection> InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken ct)
    {
        var items = new List<AccessAssignment>();

        await foreach (var assignment in _securityService.GetAccessAssignmentsAsync(_hubPath, ct))
        {
            if (assignment.IsLocal)
                items.Add(assignment);
        }

        _logger?.LogDebug("AccessAssignmentTypeSource.InitializeAsync: Loaded {Count} local assignments", items.Count);

        _lastSaved = new InstanceCollection(items.Cast<object>(), TypeDefinition.GetKey);
        return _lastSaved;
    }
}
