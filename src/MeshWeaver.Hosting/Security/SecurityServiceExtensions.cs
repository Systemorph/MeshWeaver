using MeshWeaver.Data;
using MeshWeaver.Data.Validation;
using MeshWeaver.Graph;
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
                // Per-hub scoped — each hub gets its own SecurityService instance
                // that reads from THAT hub's workspace (its synced
                // AccessAssignments collection). RlsNodeValidator runs in the
                // same scope and resolves the local SecurityService.
                services.TryAddScoped<ISecurityService, SecurityService>();
                services.AddScoped<INodeValidator, RlsNodeValidator>();

                return services;
            })
            // The synced AccessAssignments collection is intentionally NOT
            // registered here — a cross-hub query for "nodeType:AccessAssignment"
            // triggers infinite recursive hub construction at the AccessAssignment
            // NodeType hub. SecurityService instead reads static AccessAssignment
            // nodes directly from MeshConfiguration / IStaticNodeProvider at
            // construction time. Live mutations: re-construct the SecurityService
            // (or wire a separate refresh mechanism) — TODO.
            .ConfigureDefaultNodeHub(c => c
                .AddAccessControlPipeline());
    }
}
