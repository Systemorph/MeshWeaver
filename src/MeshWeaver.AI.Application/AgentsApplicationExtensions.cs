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
                // Model provider — aggregates models across every registered
                // IChatClientFactory so the dropdown matches what the runtime
                // can actually serve. Single-factory registration was the
                // source of stale model names showing in prod.
                .AddScoped<IAutocompleteProvider>(sp =>
                    new ModelAutocompleteProvider(sp.GetServices<IChatClientFactory>()))
                // Mesh catalog provider
                .AddScoped<IAutocompleteProvider>(sp =>
                    new MeshCatalogAutocompleteProvider(sp)
                )
                // Command provider
                .AddScoped<IAutocompleteProvider, CommandAutocompleteProvider>())
            .WithHandler<AutocompleteRequest>(HandleAutocompleteRequest);

    private const int AutocompleteTopN = 50;

    private static IMessageDelivery HandleAutocompleteRequest(
        IMessageHub hub,
        IMessageDelivery<AutocompleteRequest> request)
    {
        var providers = hub.ServiceProvider.GetServices<IAutocompleteProvider>();
        var query = request.Message.Query;
        var contextPath = request.Message.Context;

        // One-shot request/response back-compat: CombineLatest the providers' snapshot streams,
        // merge+score-sort into one snapshot per advance, and post the SETTLED snapshot once every
        // provider has completed (LastAsync). The progressive/streaming consumers use the
        // AutocompleteReference workspace stream instead. Catch → empty so one bad provider can't
        // stall the CombineLatest (it must still complete).
        AutocompleteSnapshots.Combine(
                providers.Select(p => p.GetItems(query, contextPath)
                    .Catch(Observable.Return(AutocompleteSnapshots.Empty))),
                AutocompleteTopN)
            .LastAsync()
            .Subscribe(snapshot => hub.Post(
                new AutocompleteResponse(snapshot.ToList()),
                o => o.ResponseFor(request)));

        return request.Processed();
    }
}
