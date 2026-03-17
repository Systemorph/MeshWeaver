using MeshWeaver.Data.Validation;
using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.Hosting.Security;

/// <summary>
/// Extension methods for configuring Row-Level Security services.
/// </summary>
public static class SecurityServiceExtensions
{
    /// <summary>
    /// Adds Row-Level Security services to the mesh.
    /// This includes:
    /// - ISecurityService for permission evaluation (uses unsecured IStorageService directly)
    /// - RlsNodeValidator for enforcing permissions on CRUD operations
    /// - AccessControlPipeline for checking RequiresPermissionAttribute on incoming messages
    /// - PersistenceService handles secure query filtering via ISecurityService (implements IMeshStorage)
    ///
    /// Storage structure:
    /// - Access/ - Global roles (Admin with null namespace) and custom role definitions
    /// - {namespace}/Access/ - Access assignments for each namespace
    /// </summary>
    public static MeshBuilder AddRowLevelSecurity(this MeshBuilder builder)
    {
        return builder
            .ConfigureServices(services =>
            {
                // Register security service (uses IStorageService directly for all storage,
                // bypassing any security filtering to avoid circular dependency)
                services.TryAddSingleton<ISecurityService, SecurityService>();

                // Register RLS validator
                services.AddSingleton<INodeValidator, RlsNodeValidator>();

                return services;
            })
            .ConfigureDefaultNodeHub(c => c.AddAccessControlPipeline());
    }
}
