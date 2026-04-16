using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Completion;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.Graph;

/// <summary>
/// Extension methods for configuring graph-related features on message hubs.
/// </summary>
public static class GraphExtensions
{
    extension(MessageHubConfiguration configuration)
    {
        /// <summary>
        /// Adds the mesh node autocomplete provider to the hub.
        /// This enables autocomplete for child mesh nodes in the hub.
        /// </summary>
        public MessageHubConfiguration AddMeshNodeAutocomplete()
            => configuration.WithServices(services =>
            {
                services.TryAddEnumerable(ServiceDescriptor.Scoped<IAutocompleteProvider, MeshNodeAutocompleteProvider>());
                return services;
            });

        /// <summary>
        /// Adds the unified reference autocomplete provider to the hub.
        /// This enables context-aware @ autocomplete for unified content references in markdown.
        /// </summary>
        public MessageHubConfiguration AddUnifiedReferenceAutocomplete()
            => configuration.WithServices(services =>
            {
                // Register concrete type once with factory (handles nullable optional dependencies)
                if (services.All(d => d.ServiceType != typeof(UnifiedReferenceAutocompleteProvider)))
                {
                    services.AddScoped(sp => new UnifiedReferenceAutocompleteProvider(
                        sp.GetService<IMeshCatalog>(),
                        sp.GetService<IMeshService>(),
                        sp.GetService<INavigationService>(),
                        sp.GetRequiredService<IMessageHub>(),
                        sp.GetService<IAutocompletePrefixRegistry>()));
                }
                services.TryAddEnumerable(ServiceDescriptor.Scoped<IAutocompleteProvider>(
                    sp => sp.GetRequiredService<UnifiedReferenceAutocompleteProvider>()));
                return services;
            });

        /// <summary>
        /// Adds both the mesh node autocomplete provider and the mesh catalog view.
        /// Convenience method for hubs that want full graph navigation support.
        /// </summary>
        public MessageHubConfiguration AddMeshNavigation()
            => configuration
                .AddMeshNodeAutocomplete()
                .AddUnifiedReferenceAutocomplete();
    }
}
