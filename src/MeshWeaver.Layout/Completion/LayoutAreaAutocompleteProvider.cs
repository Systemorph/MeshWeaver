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
        // The registered layout area definitions, with their XML-doc descriptions read through the
        // mesh's I/O pool rather than on the caller's thread.
        var layoutDefinition = uiControlService.LayoutDefinition;
        var address = hub.Address;
        var addressStr = address.ToString();

        return layoutDefinition.ObserveVisibleAreaDefinitions().Select(areas => (IReadOnlyCollection<AutocompleteItem>)areas
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
            .ToList());
    }
}
