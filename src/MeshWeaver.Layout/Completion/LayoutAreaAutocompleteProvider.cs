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
    public Task<IEnumerable<AutocompleteItem>> GetItemsAsync(string query, CancellationToken ct = default)
    {
        var layoutDefinition = uiControlService.LayoutDefinition;
        var address = hub.Address;

        // Format: addressType/addressId/areaName (area is the default, no keyword needed)
        var areas = layoutDefinition.AreaDefinitions.Values
            .Where(area => area.IsInvisible != true && !area.Area.StartsWith("$"))
            .Select(area => new AutocompleteItem(
                Label: area.Title ?? area.Area,
                InsertText: $"@{address}/{area.Area} ",
                Description: area.Description ?? $"Layout area: {area.Area}",
                Category: area.Group ?? "Layout Areas",
                Priority: area.Order ?? 0,
                Kind: AutocompleteKind.Other
            ));

        return Task.FromResult(areas);
    }
}
