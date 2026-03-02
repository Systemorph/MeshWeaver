using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;

namespace Memex.Portal.Shared.Admin;

/// <summary>
/// Service providing typed access to Admin namespace nodes via IPersistenceService.
/// Platform admin checks use standard AccessAssignment nodes with the PlatformAdmin role.
/// </summary>
public class AdminService
{
    private readonly IPersistenceService _persistence;

    public const string AdminNamespace = "Admin";
    public const string InitializationPath = "Admin/Initialization";
    public const string AuthProvidersPath = "Admin/AuthProviders";

    public AdminService(IPersistenceService persistence)
    {
        _persistence = persistence;
    }

    public async Task<bool> IsInitializedAsync(CancellationToken ct = default)
    {
        return await _persistence.ExistsAsync(InitializationPath, ct);
    }

    public async Task<InitializationContent?> GetInitializationAsync(CancellationToken ct = default)
    {
        var node = await _persistence.GetNodeAsync(InitializationPath, ct);
        return DeserializeContent<InitializationContent>(node?.Content);
    }

    public async Task InitializeAsync(
        string userId,
        AuthProviderSettings? authProviders = null,
        List<string>? adminUsers = null,
        CancellationToken ct = default)
    {
        // Create Admin namespace node if it doesn't exist
        if (!await _persistence.ExistsAsync(AdminNamespace, ct))
        {
            await _persistence.SaveNodeAsync(new MeshNode(AdminNamespace)
            {
                Name = "Admin",
                NodeType = "Markdown",
                State = MeshNodeState.Active
            }, ct);
        }

        // Create Initialization node
        var content = new InitializationContent
        {
            Version = "3.0",
            InitializedAt = DateTimeOffset.UtcNow,
            InitializedBy = userId
        };

        await _persistence.SaveNodeAsync(new MeshNode("Initialization", AdminNamespace)
        {
            Name = "Initialization",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = content
        }, ct);

        // Create AuthProviders node
        await SaveAuthProviderSettingsAsync(
            authProviders ?? new AuthProviderSettings { EnableDevLogin = true }, ct);

        // Create PlatformAdmin access assignments for admin users
        var users = adminUsers ?? [userId];
        foreach (var user in users)
        {
            await SavePlatformAdminAccessAsync(user, ct);
        }
    }

    public async Task<AuthProviderSettings> GetAuthProviderSettingsAsync(CancellationToken ct = default)
    {
        var node = await _persistence.GetNodeAsync(AuthProvidersPath, ct);
        return DeserializeContent<AuthProviderSettings>(node?.Content)
               ?? new AuthProviderSettings();
    }

    public async Task SaveAuthProviderSettingsAsync(AuthProviderSettings settings, CancellationToken ct = default)
    {
        await _persistence.SaveNodeAsync(new MeshNode("AuthProviders", AdminNamespace)
        {
            Name = "Auth Providers",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = settings
        }, ct);
    }

    /// <summary>
    /// Checks if a user is a platform admin by looking for a PlatformAdmin role
    /// assignment in the Admin namespace via ISecurityService.
    /// </summary>
    public static async Task<bool> IsAdminAsync(ISecurityService securityService, string userId, CancellationToken ct = default)
    {
        var permissions = await securityService.HasPermissionAsync(AdminNamespace, userId, Permission.All, ct);
        return permissions;
    }

    /// <summary>
    /// Creates or updates a PlatformAdmin AccessAssignment for a user in the Admin namespace.
    /// </summary>
    public async Task SavePlatformAdminAccessAsync(string userId, CancellationToken ct = default)
    {
        var nodeId = $"{userId}_Access";
        await _persistence.SaveNodeAsync(new MeshNode(nodeId, AdminNamespace)
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
