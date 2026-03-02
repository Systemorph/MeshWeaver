using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;

namespace Memex.Portal.Shared.Admin;

/// <summary>
/// Service providing typed access to the Admin node (nodeType "Platform")
/// and its PlatformSettings content via IPersistenceService.
/// </summary>
public class AdminService
{
    private readonly IPersistenceService _persistence;

    public const string AdminPath = "Admin";

    public AdminService(IPersistenceService persistence)
    {
        _persistence = persistence;
    }

    public async Task<bool> IsInitializedAsync(CancellationToken ct = default)
    {
        var node = await _persistence.GetNodeAsync(AdminPath, ct);
        return node?.Content != null;
    }

    public async Task<PlatformSettings> GetPlatformSettingsAsync(CancellationToken ct = default)
    {
        var node = await _persistence.GetNodeAsync(AdminPath, ct);
        return DeserializeContent<PlatformSettings>(node?.Content) ?? new PlatformSettings();
    }

    public async Task SavePlatformSettingsAsync(PlatformSettings settings, CancellationToken ct = default)
    {
        // Only persist providers that are actually configured
        var filteredProviders = settings.Providers
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value.AppId))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var filtered = settings with { Providers = filteredProviders };

        await _persistence.SaveNodeAsync(new MeshNode(AdminPath)
        {
            Name = "Platform",
            NodeType = PlatformNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = filtered
        }, ct);
    }

    public async Task InitializeAsync(
        string userId,
        PlatformSettings? settings = null,
        List<string>? adminUsers = null,
        CancellationToken ct = default)
    {
        var platformSettings = (settings ?? new PlatformSettings()) with
        {
            InitializedAt = DateTimeOffset.UtcNow,
            InitializedBy = userId
        };

        await SavePlatformSettingsAsync(platformSettings, ct);

        // Create PlatformAdmin access assignments for admin users
        var users = adminUsers ?? [userId];
        foreach (var user in users)
        {
            await SavePlatformAdminAccessAsync(user, ct);
        }
    }

    /// <summary>
    /// Checks if a user is a platform admin via ISecurityService.
    /// </summary>
    public static async Task<bool> IsAdminAsync(ISecurityService securityService, string userId, CancellationToken ct = default)
    {
        return await securityService.HasPermissionAsync(AdminPath, userId, Permission.All, ct);
    }

    /// <summary>
    /// Creates or updates a PlatformAdmin AccessAssignment for a user in the Admin namespace.
    /// </summary>
    public async Task SavePlatformAdminAccessAsync(string userId, CancellationToken ct = default)
    {
        var nodeId = $"{userId}_Access";
        await _persistence.SaveNodeAsync(new MeshNode(nodeId, AdminPath)
        {
            Name = $"{userId} Access",
            NodeType = "AccessAssignment",
            State = MeshNodeState.Active,
            Content = new AccessAssignment
            {
                AccessObject = userId,
                DisplayName = userId,
                Roles = [new RoleAssignment { Role = "PlatformAdmin" }]
            }
        }, ct);
    }

    private static T? DeserializeContent<T>(object? content) where T : class
    {
        if (content is T typed)
            return typed;
        if (content is JsonElement jsonElement)
            return JsonSerializer.Deserialize<T>(jsonElement.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return null;
    }
}
