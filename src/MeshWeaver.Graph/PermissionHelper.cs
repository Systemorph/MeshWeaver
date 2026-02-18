using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Centralized permission checking helper used by layout areas.
/// When ISecurityService is not configured (RLS disabled), returns Permission.All.
/// </summary>
public static class PermissionHelper
{
    /// <summary>
    /// Gets the effective permissions for the current user on a node path.
    /// Returns Permission.All if RLS is not configured.
    /// </summary>
    public static async Task<Permission> GetEffectivePermissionsAsync(IMessageHub hub, string nodePath)
    {
        var securityService = hub.ServiceProvider.GetService<ISecurityService>();
        if (securityService == null)
            return Permission.All;

        try
        {
            return await securityService.GetEffectivePermissionsAsync(nodePath);
        }
        catch
        {
            return Permission.All; // Fallback: allow on error
        }
    }

    /// <summary>
    /// Checks if the current user has Update permission on a node path.
    /// </summary>
    public static async Task<bool> CanEditAsync(IMessageHub hub, string nodePath)
    {
        var permissions = await GetEffectivePermissionsAsync(hub, nodePath);
        return permissions.HasFlag(Permission.Update);
    }

    /// <summary>
    /// Checks if the current user has Create permission on a parent path.
    /// </summary>
    public static async Task<bool> CanCreateAsync(IMessageHub hub, string parentPath)
    {
        var permissions = await GetEffectivePermissionsAsync(hub, parentPath);
        return permissions.HasFlag(Permission.Create);
    }

    /// <summary>
    /// Checks if the current user has Delete permission on a node path.
    /// </summary>
    public static async Task<bool> CanDeleteAsync(IMessageHub hub, string nodePath)
    {
        var permissions = await GetEffectivePermissionsAsync(hub, nodePath);
        return permissions.HasFlag(Permission.Delete);
    }

    /// <summary>
    /// Checks if the current user has Comment permission on a parent path.
    /// Update permission implies Comment permission.
    /// </summary>
    public static async Task<bool> CanCommentAsync(IMessageHub hub, string parentPath)
    {
        var permissions = await GetEffectivePermissionsAsync(hub, parentPath);
        return permissions.HasFlag(Permission.Comment) || permissions.HasFlag(Permission.Update);
    }
}
