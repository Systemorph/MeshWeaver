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
    /// - ISecurityService for permission evaluation
    /// - RlsNodeValidator for enforcing permissions on CRUD operations
    /// - SecurePersistenceServiceDecorator for filtered queries
    /// </summary>
    public static MeshBuilder AddRowLevelSecurity(this MeshBuilder builder)
    {
        return builder.ConfigureServices(services =>
        {
            // Register storage adapter
            services.TryAddSingleton<SecurityStorageAdapter>();

            // Register security service
            services.TryAddSingleton<ISecurityService, SecurityService>();

            // Register RLS validator
            services.AddSingleton<INodeValidator, RlsNodeValidator>();

            return services;
        });
    }

    /// <summary>
    /// Configures role settings for a NodeType.
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <param name="nodeType">The NodeType to configure</param>
    /// <param name="configure">Configuration action</param>
    public static MeshBuilder WithRoleConfiguration(
        this MeshBuilder builder,
        string nodeType,
        Action<RoleConfigurationBuilder> configure)
    {
        var configBuilder = new RoleConfigurationBuilder(nodeType);
        configure(configBuilder);
        var config = configBuilder.Build();

        return builder.ConfigureServices(services =>
        {
            // Store the configuration to be loaded by SecurityService
            services.AddSingleton(config);
            return services;
        });
    }

    /// <summary>
    /// Adds a global role assignment that applies across the mesh.
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <param name="userId">The user's ObjectId</param>
    /// <param name="roleId">The role to assign</param>
    public static MeshBuilder WithGlobalRoleAssignment(
        this MeshBuilder builder,
        string userId,
        string roleId)
    {
        var assignment = new RoleAssignment
        {
            UserId = userId,
            RoleId = roleId,
            NodePath = null, // Global
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "system"
        };

        return builder.ConfigureServices(services =>
        {
            services.AddSingleton(assignment);
            return services;
        });
    }
}

/// <summary>
/// Builder for constructing RoleConfiguration instances.
/// </summary>
public class RoleConfigurationBuilder
{
    private readonly string _nodeType;
    private readonly Dictionary<string, Role> _roles = new();
    private bool _isPublic = false;
    private Permission _anonymousPermissions = Permission.None;
    private bool _inheritFromParent = true;

    public RoleConfigurationBuilder(string nodeType)
    {
        _nodeType = nodeType;
    }

    /// <summary>
    /// Adds a role to this NodeType configuration.
    /// </summary>
    public RoleConfigurationBuilder WithRole(Role role)
    {
        _roles[role.Id] = role;
        return this;
    }

    /// <summary>
    /// Adds a built-in Admin role.
    /// </summary>
    public RoleConfigurationBuilder WithAdminRole()
    {
        _roles["Admin"] = Role.Admin;
        return this;
    }

    /// <summary>
    /// Adds a built-in Editor role.
    /// </summary>
    public RoleConfigurationBuilder WithEditorRole()
    {
        _roles["Editor"] = Role.Editor;
        return this;
    }

    /// <summary>
    /// Adds a built-in Viewer role.
    /// </summary>
    public RoleConfigurationBuilder WithViewerRole()
    {
        _roles["Viewer"] = Role.Viewer;
        return this;
    }

    /// <summary>
    /// Makes nodes of this type publicly accessible.
    /// </summary>
    /// <param name="permissions">Permissions for anonymous users (defaults to Read)</param>
    public RoleConfigurationBuilder AsPublic(Permission permissions = Permission.Read)
    {
        _isPublic = true;
        _anonymousPermissions = permissions;
        return this;
    }

    /// <summary>
    /// Disables permission inheritance from parent nodes.
    /// </summary>
    public RoleConfigurationBuilder WithoutInheritance()
    {
        _inheritFromParent = false;
        return this;
    }

    /// <summary>
    /// Enables permission inheritance from parent nodes (default).
    /// </summary>
    public RoleConfigurationBuilder WithInheritance()
    {
        _inheritFromParent = true;
        return this;
    }

    /// <summary>
    /// Builds the RoleConfiguration.
    /// </summary>
    public RoleConfiguration Build() => new()
    {
        NodeType = _nodeType,
        Roles = _roles,
        IsPublic = _isPublic,
        AnonymousPermissions = _anonymousPermissions,
        InheritFromParent = _inheritFromParent
    };
}
