#nullable enable

using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data.Completion;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Client that dispatches autocomplete requests to hub addresses.
/// Always dispatches to base addresses plus context address.
/// Each provider is responsible for filtering its results based on the query.
/// </summary>
public class AutocompleteClient(
    IMessageHub hub,
    Func<AgentContext?, IReadOnlyCollection<Address>> getBaseAddresses)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets autocomplete suggestions by dispatching requests to all configured addresses.
    /// Per-address responses merge through Observable.Merge — no per-address Task await,
    /// no <c>.ToTask()</c> bridge on hub round-trips.
    /// </summary>
    public IObservable<AutocompleteResponse> GetCompletions(
        string query,
        AgentContext? context)
    {
        var addresses = GetAllDispatchAddresses(context);

        var perAddress = addresses
            .Select(address =>
            {
                var delivery = hub.Post(
                    new AutocompleteRequest(query, context?.Context),
                    o => o.WithTarget(address));
                if (delivery == null)
                    return Observable.Return<IReadOnlyList<AutocompleteItem>>(Array.Empty<AutocompleteItem>());

                return hub.Observe(delivery)
                    .Timeout(DefaultTimeout)
                    .FirstAsync()
                    .Select(d => d.Message is AutocompleteResponse { Items: { } items }
                        ? items
                        : Array.Empty<AutocompleteItem>())
                    // Tolerate hub-level failures (target unreachable, timeout as DeliveryFailure)
                    // and any unexpected response type — skipping is the historical behaviour.
                    .Catch<IReadOnlyList<AutocompleteItem>, Exception>(
                        _ => Observable.Return<IReadOnlyList<AutocompleteItem>>(Array.Empty<AutocompleteItem>()));
            });

        return perAddress
            .Merge()
            .Aggregate(ImmutableList<AutocompleteItem>.Empty, (acc, items) => acc.AddRange(items))
            .Select(allItems =>
            {
                // Deduplicate by InsertText (keep highest priority item)
                var deduplicated = allItems
                    .GroupBy(i => i.InsertText)
                    .Select(g => g.OrderByDescending(i => i.Priority).First())
                    .ToImmutableList();
                return new AutocompleteResponse(deduplicated);
            });
    }

    /// <summary>
    /// Gets all addresses to dispatch to: base addresses + context address.
    /// </summary>
    private IReadOnlyCollection<Address> GetAllDispatchAddresses(AgentContext? context)
    {
        var addresses = ImmutableHashSet<Address>.Empty;

        // Add base addresses (app/Agents)
        foreach (var addr in getBaseAddresses(context))
        {
            addresses = addresses.Add(addr);
        }

        // Add context address if present
        if (context?.Address != null)
        {
            addresses = addresses.Add(context.Address);
        }

        return addresses;
    }
}
