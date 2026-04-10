using System.Runtime.CompilerServices;
using MeshWeaver.Data.Completion;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Completion;

/// <summary>
/// Provides autocomplete items for layout areas.
/// Returns all visible layout areas registered via AddLayout().
/// </summary>
public class LayoutAreaAutocompleteProvider(IUiControlService uiControlService, IMessageHub hub) : IAutocompleteProvider
{
    /// <inheritdoc />
    public async IAsyncEnumerable<AutocompleteItem> GetItemsAsync(
        string query,
        string? contextPath = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask; // Satisfy async requirement

        var layoutDefinition = uiControlService.LayoutDefinition;
        var address = hub.Address;
        var addressStr = address.ToString();

        // Format: addressType/addressId/areaName (area is the default, no keyword needed)
        var areas = layoutDefinition.AreaDefinitions.Values
            .Where(area => area.IsInvisible != true && !area.Area.StartsWith("$"));

        foreach (var area in areas)
        {
            var priority = area.Order ?? 500;

            // Proximity boost: if contextPath is within the same address
            if (!string.IsNullOrEmpty(contextPath) &&
                !string.IsNullOrEmpty(addressStr) &&
                (contextPath.Equals(addressStr, StringComparison.OrdinalIgnoreCase) ||
                 contextPath.StartsWith(addressStr + "/", StringComparison.OrdinalIgnoreCase)))
            {
                priority += 1000; // local layout area
            }

            yield return new AutocompleteItem(
                Label: area.Title ?? area.Area,
                InsertText: $"@{address}/{area.Area} ",
                Description: area.Description ?? $"Layout area: {area.Area}",
                Category: area.Group ?? "Layout Areas",
                Priority: priority,
                Kind: AutocompleteKind.Other
            );
        }
    }
}
