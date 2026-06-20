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
                    // Collapse to empty so one bad provider can't stall the
                    // CombineLatest — Debug-logged so the fault stays greppable.
                    .Catch((Exception ex) =>
                    {
                        hub.ServiceProvider.GetService<ILoggerFactory>()
                            ?.CreateLogger(typeof(AgentsApplicationExtensions))
                            .LogDebug(ex, "Autocomplete provider {Provider} faulted — returning empty",
                                p.GetType().Name);
                        return Observable.Return(AutocompleteSnapshots.Empty);
                    })),
                AutocompleteTopN)
            .LastAsync()
            .Subscribe(
                snapshot => hub.Post(
                    new AutocompleteResponse(snapshot.ToList()),
                    o => o.ResponseFor(request)),
                ex =>
                {
                    // The caller is waiting on a response — answer empty rather
                    // than letting it time out, and log the combine fault.
                    hub.ServiceProvider.GetService<ILoggerFactory>()
                        ?.CreateLogger(typeof(AgentsApplicationExtensions))
                        .LogWarning(ex, "Autocomplete combine faulted for query '{Query}'", query);
                    hub.Post(
                        new AutocompleteResponse([]),
                        o => o.ResponseFor(request));
                });

        return request.Processed();
    }
}
