#nullable enable

namespace MeshWeaver.Data.Completion;

/// <summary>
/// Response containing autocomplete suggestions.
/// </summary>
/// <param name="Items">The autocomplete items to display.</param>
public record AutocompleteResponse(IReadOnlyList<AutocompleteItem> Items);
