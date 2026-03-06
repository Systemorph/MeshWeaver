using MeshWeaver.AI.Application.Layout;
using MeshWeaver.AI.Completion;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Completion;
using MeshWeaver.Mesh.Services;
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
                // Model provider - uses chat client factory if available
                .AddScoped<IAutocompleteProvider>(sp =>
                {
                    var chatClientFactory = sp.GetService<IChatClientFactory>();
                    if (chatClientFactory != null)
                        return new ModelAutocompleteProvider(chatClientFactory);
                    return new ModelAutocompleteProvider();
                })
                // Mesh catalog provider
                .AddScoped<IAutocompleteProvider>(sp =>
                    new MeshCatalogAutocompleteProvider(sp)
                )
                // Command provider
                .AddScoped<IAutocompleteProvider, CommandAutocompleteProvider>())
            .WithHandler<AutocompleteRequest>(HandleAutocompleteRequest);

    private static async Task<IMessageDelivery> HandleAutocompleteRequest(
        IMessageHub hub,
        IMessageDelivery<AutocompleteRequest> request,
        CancellationToken ct)
    {
        var providers = hub.ServiceProvider.GetServices<IAutocompleteProvider>();
        var query = request.Message.Query;
        var contextPath = request.Message.Context;

        var allItems = new List<AutocompleteItem>();
        foreach (var provider in providers)
        {
            try
            {
                await foreach (var item in provider.GetItemsAsync(query, contextPath, ct))
                {
                    allItems.Add(item);
                }
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
