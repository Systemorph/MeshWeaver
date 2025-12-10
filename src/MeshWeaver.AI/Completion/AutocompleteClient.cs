#nullable enable

using MeshWeaver.Data;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Client that dispatches autocomplete requests to hub addresses.
/// Always dispatches to base addresses plus all namespace addresses that have autocomplete configured.
/// Each provider is responsible for filtering its results based on the query.
/// </summary>
public class AutocompleteClient(
    IMessageHub hub,
    Func<AgentContext?, IReadOnlyCollection<Address>> getBaseAddresses,
    IMeshCatalog? meshCatalog = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets autocomplete suggestions by dispatching requests to all configured addresses.
    /// </summary>
    public async Task<AutocompleteResponse> GetCompletionsAsync(
        string query,
        AgentContext? context,
        CancellationToken ct = default)
    {
        var allItems = new List<AutocompleteItem>();

        // Get all addresses to query
        var addresses = await GetAllDispatchAddressesAsync(context, ct);

        foreach (var address in addresses)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(DefaultTimeout);

                var response = await hub.AwaitResponse(
                    new AutocompleteRequest(query, context?.Context),
                    o => o.WithTarget(address),
                    timeoutCts.Token);

                if (response?.Message?.Items != null)
                {
                    allItems.AddRange(response.Message.Items);
                }
            }
            catch
            {
                // Skip addresses that fail to respond or timeout
            }
        }

        // Deduplicate by InsertText (keep highest priority item)
        var deduplicated = allItems
            .GroupBy(i => i.InsertText)
            .Select(g => g.OrderByDescending(i => i.Priority).First())
            .ToList();

        return new AutocompleteResponse(deduplicated);
    }

    /// <summary>
    /// Gets all addresses to dispatch to: base addresses + all namespace addresses + context address.
    /// </summary>
    private async Task<IReadOnlyCollection<Address>> GetAllDispatchAddressesAsync(
        AgentContext? context,
        CancellationToken ct)
    {
        var addresses = new HashSet<Address>();

        // Add base addresses (app/Agents)
        foreach (var addr in getBaseAddresses(context))
        {
            addresses.Add(addr);
        }

        // Add all namespace autocomplete addresses
        if (meshCatalog != null)
        {
            var namespaces = await meshCatalog.GetNamespacesAsync(ct);
            foreach (var ns in namespaces)
            {
                var nsAddress = ns.AutocompleteAddress?.Invoke(context);
                if (nsAddress != null)
                {
                    addresses.Add(nsAddress);
                }
            }
        }

        // Add context address if present
        if (context?.Address != null)
        {
            addresses.Add(context.Address);
        }

        return addresses.ToList();
    }
}
