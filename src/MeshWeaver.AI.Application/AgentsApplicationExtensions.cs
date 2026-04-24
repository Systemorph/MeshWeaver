using System.Reactive.Linq;
using MeshWeaver.AI.Application.Layout;
using MeshWeaver.AI.Completion;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Completion;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
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

    // Higher Priority = better. Sort descending so best comes first.
    private static readonly IComparer<AutocompleteItem> AutocompleteByPriority =
        Comparer<AutocompleteItem>.Create((a, b) => b.Priority.CompareTo(a.Priority));

    private const int AutocompleteTopN = 50;

    private static IMessageDelivery HandleAutocompleteRequest(
        IMessageHub hub,
        IMessageDelivery<AutocompleteRequest> request)
    {
        var providers = hub.ServiceProvider.GetServices<IAutocompleteProvider>();
        var query = request.Message.Query;
        var contextPath = request.Message.Context;

        // Request-response: AutocompleteRequest expects exactly one AutocompleteResponse.
        // Merge every provider's IAsyncEnumerable into one observable stream, fold into a
        // top-N sorted snapshot via ScanTopN, and wait until all providers complete (Last)
        // before posting the final aggregated response. No await, no Task — the observable
        // chain drives the post when the source completes.
        providers
            .Select(p => p.GetItemsAsync(query, contextPath, default)
                .ToObservableSequence()
                .Catch(Observable.Empty<AutocompleteItem>()))
            .Merge()
            .ScanTopN(AutocompleteTopN, AutocompleteByPriority)
            .LastOrDefaultAsync()
            .Subscribe(snapshot => hub.Post(
                new AutocompleteResponse((snapshot ?? Array.Empty<AutocompleteItem>()).ToList()),
                o => o.ResponseFor(request)));

        return request.Processed();
    }
}
