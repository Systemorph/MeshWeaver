using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
    /// - SecurePersistenceServiceDecorator for filtering query results
    /// - Per-namespace Access partition storage via IPersistenceService
    ///
    /// Storage structure:
    /// - Access/ - Global roles (Admin with null namespace) and custom role definitions
    /// - {namespace}/Access/ - UserAccess records for each namespace
    /// </summary>
    public static MeshBuilder AddRowLevelSecurity(this MeshBuilder builder)
    {
        return builder.ConfigureServices(services =>
        {
            // Register security service (uses IPersistenceService directly for all storage)
            services.TryAddSingleton<ISecurityService, SecurityService>();

            // Register RLS validator
            services.AddSingleton<INodeValidator, RlsNodeValidator>();

            // Decorate IPersistenceService with security filtering
            DecorateWithSecurity(services);

            return services;
        });
    }

    /// <summary>
    /// Decorates IPersistenceService with SecurePersistenceServiceDecorator.
    /// </summary>
    private static void DecorateWithSecurity(IServiceCollection services)
    {
        // Find the existing IPersistenceService registration
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IPersistenceService));
        if (descriptor == null)
            return; // No persistence service registered yet

        // Remove the original registration
        services.Remove(descriptor);

        // Add the decorator that wraps the original
        services.Add(ServiceDescriptor.Describe(
            typeof(IPersistenceService),
            sp =>
            {
                // Create the original service
                var inner = descriptor.ImplementationFactory != null
                    ? (IPersistenceService)descriptor.ImplementationFactory(sp)
                    : descriptor.ImplementationInstance != null
                        ? (IPersistenceService)descriptor.ImplementationInstance
                        : (IPersistenceService)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);

                // Wrap it with the security decorator (use Lazy to avoid circular dependency)
                return new SecurePersistenceServiceDecorator(
                    inner,
                    new Lazy<ISecurityService>(() => sp.GetRequiredService<ISecurityService>()),
                    sp.GetRequiredService<ILogger<SecurePersistenceServiceDecorator>>());
            },
            descriptor.Lifetime));
    }
}
