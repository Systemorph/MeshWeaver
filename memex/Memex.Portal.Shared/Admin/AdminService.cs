using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace Memex.Portal.Shared.Admin;

/// <summary>
/// Service providing typed access to Admin namespace nodes via IPersistenceService.
/// </summary>
public class AdminService
{
    private readonly IPersistenceService _persistence;

    public const string AdminNamespace = "Admin";
    public const string InitializationPath = "Admin/Initialization";
    public const string AuthProvidersPath = "Admin/AuthProviders";
    public const string SettingsPath = "Admin/Settings";

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
        AdminSettings? adminSettings = null,
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

        // Create Settings node with initializing user as admin
        await SaveAdminSettingsAsync(
            adminSettings ?? new AdminSettings { AdminUsers = [userId] }, ct);
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

    public async Task<AdminSettings> GetAdminSettingsAsync(CancellationToken ct = default)
    {
        var node = await _persistence.GetNodeAsync(SettingsPath, ct);
        return DeserializeContent<AdminSettings>(node?.Content)
               ?? new AdminSettings();
    }

    public async Task SaveAdminSettingsAsync(AdminSettings settings, CancellationToken ct = default)
    {
        await _persistence.SaveNodeAsync(new MeshNode("Settings", AdminNamespace)
        {
            Name = "Settings",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = settings
        }, ct);
    }

    public async Task<bool> IsAdminAsync(string userId, CancellationToken ct = default)
    {
        var settings = await GetAdminSettingsAsync(ct);
        return settings.AdminUsers.Contains(userId, StringComparer.OrdinalIgnoreCase);
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
