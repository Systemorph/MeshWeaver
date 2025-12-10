using MeshWeaver.AI.Application.Layout;
using MeshWeaver.AI.Completion;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Completion;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

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
                .AddScoped<IAutocompleteProvider, AgentAutocompleteProvider>()
                .AddScoped<IAutocompleteProvider, ModelAutocompleteProvider>()
                .AddScoped<IAutocompleteProvider, MeshCatalogAutocompleteProvider>()
                .AddScoped<IAutocompleteProvider, CommandAutocompleteProvider>())
            .WithHandler<AutocompleteRequest>(HandleAutocompleteRequest);

    private static async Task<IMessageDelivery> HandleAutocompleteRequest(
        IMessageHub hub,
        IMessageDelivery<AutocompleteRequest> request,
        CancellationToken ct)
    {
        var providers = hub.ServiceProvider.GetServices<IAutocompleteProvider>();
        var query = request.Message.Query;

        var allItems = new List<AutocompleteItem>();
        foreach (var provider in providers)
        {
            try
            {
                var items = await provider.GetItemsAsync(query, ct);
                allItems.AddRange(items);
            }
            catch
            {
                // Skip providers that fail
            }
        }

        var response = new AutocompleteResponse(allItems);
        hub.Post(response, o => o.ResponseFor(request));
        return request.Processed();
    }
}
