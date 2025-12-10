#nullable enable

using MeshWeaver.Data;
using MeshWeaver.Data.Completion;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Client that dispatches autocomplete requests to configured hub addresses.
/// Uses a lambda function to determine which addresses to query based on the current context.
/// </summary>
public class AutocompleteClient(
    IMessageHub hub,
    Func<AgentContext?, IReadOnlyCollection<Address>> getDispatchAddresses)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets autocomplete suggestions by dispatching requests to configured addresses.
    /// </summary>
    /// <param name="query">The search query (text being typed).</param>
    /// <param name="context">The current agent context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregated autocomplete response from all dispatched addresses.</returns>
    public async Task<AutocompleteResponse> GetCompletionsAsync(
        string query,
        AgentContext? context,
        CancellationToken ct = default)
    {
        var allItems = new List<AutocompleteItem>();
        var addresses = getDispatchAddresses(context);

        foreach (var address in addresses)
        {
            try
            {
                // Use a timeout to avoid hanging if the target hub doesn't exist or respond
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
}
