using System.Reactive.Linq;
using MeshWeaver.AI.Application.Layout;
using MeshWeaver.AI.Completion;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Completion;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Application;

/// <summary>
/// Extensions for creating the agents application
/// </summary>
public static class AgentsApplicationExtensions
{
    /// <summary>
    /// Full configuration of the Agents application mesh node.
    /// </summary>
    /// <param name="application">The message hub configuration</param>
    /// <returns>Configured message hub</returns>
    public static MessageHubConfiguration ConfigureAgentsApplication(this MessageHubConfiguration application)
        => application
            .AddAIViews()
            .WithServices(services => services
                // Mesh catalog provider — @-references autocomplete from the mesh node
                // catalog (agents, models, and every other node). The old factory-based
                // ModelAutocompleteProvider was deleted: models are mesh nodes now, so it
                // only duplicated what this provider already lists.
                .AddScoped<IAutocompleteProvider>(sp =>
                    new MeshCatalogAutocompleteProvider(sp)
                )
                // Skill provider — slash skills (/agent, /model, /harness, …) from the nodeType:Skill catalog.
                .AddScoped<IAutocompleteProvider, SkillAutocompleteProvider>())
            // NO WithHandler<AutocompleteRequest> here. MeshWeaver.Data registers that handler on
            // every hub already, with relevance scoring this copy never had — and a second
            // registration for the same request type is how the two drifted. What this app owes the
            // handler is the PROVIDERS above; the platform owns answering (#2276).
            ;

}
