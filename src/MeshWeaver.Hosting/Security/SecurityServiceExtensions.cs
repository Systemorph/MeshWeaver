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
/// Extension methods for configuring Row-Level Security.
/// </summary>
public static class SecurityServiceExtensions
{
    /// <summary>
    /// Adds Row-Level Security to the mesh. Registers:
    /// <list type="bullet">
    /// <item><see cref="EffectivePermissionsDelegate"/> →
    ///   <see cref="PermissionEvaluator.GetEffectivePermissions(MeshWeaver.Messaging.IMessageHub, string, string)"/> injected
    ///   into every per-node hub's <c>MessageHubConfiguration</c>, so every
    ///   <c>hub.CheckPermission</c> / <c>hub.GetEffectivePermissions</c>
    ///   resolves the real algorithm. Without this registration the default
    ///   delegate returns <c>Permission.All</c> (no gating). Consumers that
    ///   need a feature-flag check ask
    ///   <c>hub.Configuration.Get&lt;EffectivePermissionsDelegate&gt;() is not null</c>.</item>
    /// <item><see cref="RlsNodeValidator"/> — enforces permissions on CRUD
    ///   operations through <c>hub.CheckPermission</c>.</item>
    /// <item><see cref="AccessControlPipeline"/> — request-time permission
    ///   pipeline for inbound messages on every per-node hub.</item>
    /// </list>
    ///
    /// <para>The algorithm itself lives in <see cref="PermissionEvaluator"/>
    /// (Mesh.Contract) as static functions over <see cref="IMeshNodeStreamCache"/>.
    /// There is no <c>SecurityService</c> class anymore — application code
    /// MUST go through <c>HubPermissionExtensions</c>.</para>
    /// </summary>
    public static MeshBuilder AddRowLevelSecurity(this MeshBuilder builder)
    {
        return builder
            .ConfigureServices(services =>
            {
                services.AddScoped<INodeValidator, RlsNodeValidator>();
                // Structural partition guard: blocks writes into the system-managed
                // User/Auth mirror and prevents implicit space creation ("no partition,
                // no write"). Runs alongside RlsNodeValidator — a rejection here wins
                // even when RLS would grant (validators AND-compose).
                services.AddScoped<INodeValidator, PartitionWriteGuardValidator>();
                // The ONE place a partition schema is created: creating a top-level
                // instance of an OwnsPartition NodeType (User, Space) eagerly provisions
                // its schema BEFORE the root write. Mandatory now that the storage router
                // no longer lazily CREATE SCHEMAs on arbitrary writes (the atioz ghost-
                // schema fix). See Doc/Architecture/PartitionStorageRouting.md.
                services.AddScoped<INodeValidator, OwnsPartitionProvisioningValidator>();
                return services;
            })
            // Mesh hub: needed wherever code calls hub.CheckPermission on the
            // mesh address (delete handlers, routing-time checks, tests).
            .ConfigureHub(c => c.AddRowLevelSecurity())
            // Per-node hubs: RLS + AccessControlPipeline for request-time
            // permission checks.
            .ConfigureDefaultNodeHub(c => c
                .AddRowLevelSecurity()
                .AddAccessControlPipeline());
    }
}
