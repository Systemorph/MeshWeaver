using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Completion;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

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
                services.AddScoped<IAutocompleteProvider, MeshNodeAutocompleteProvider>());

        /// <summary>
        /// Adds the unified reference autocomplete provider to the hub.
        /// This enables context-aware @ autocomplete for unified content references in markdown.
        /// </summary>
        public MessageHubConfiguration AddUnifiedReferenceAutocomplete()
            => configuration.WithServices(services =>
                services.AddScoped<IAutocompleteProvider, UnifiedReferenceAutocompleteProvider>());

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
