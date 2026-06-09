using System.Reactive.Linq;
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
    public IObservable<IReadOnlyCollection<AutocompleteItem>> GetItems(string query, string? contextPath = null)
    {
        // Pure in-memory enumeration of registered layout area definitions.
        var layoutDefinition = uiControlService.LayoutDefinition;
        var address = hub.Address;
        var addressStr = address.ToString();

        var items = layoutDefinition.AreaDefinitions.Values
            .Where(area => area.IsInvisible != true && !area.Area.StartsWith("$"))
            .Select(area =>
            {
                var priority = area.Order ?? 500;

                if (!string.IsNullOrEmpty(contextPath) &&
                    !string.IsNullOrEmpty(addressStr) &&
                    (contextPath.Equals(addressStr, StringComparison.OrdinalIgnoreCase) ||
                     contextPath.StartsWith(addressStr + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    priority += 1000; // local layout area
                }

                return new AutocompleteItem(
                    Label: area.Title ?? area.Area,
                    InsertText: $"@{address}/{area.Area} ",
                    Description: area.Description ?? $"Layout area: {area.Area}",
                    Category: area.Group ?? "Layout Areas",
                    Priority: priority,
                    Kind: AutocompleteKind.Other);
            })
            .ToList();
        return Observable.Return((IReadOnlyCollection<AutocompleteItem>)items);
    }
}
