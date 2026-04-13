#nullable enable

namespace MeshWeaver.Data.Completion;

/// <summary>
/// Registry of autocomplete prefix segments that have dedicated providers.
/// Generic providers (UnifiedReferenceAutocompleteProvider, MeshNodeAutocompleteProvider)
/// query this to skip queries handled by specialized providers like ContentAutocompleteProvider.
///
/// Implementations typically aggregate <see cref="IAutocompleteProvider.Prefix"/> values
/// from all registered providers in DI.
/// </summary>
public interface IAutocompletePrefixRegistry
{
    /// <summary>
    /// Returns true if the given segment is a registered prefix
    /// (e.g., "content", "data", "schema") handled by a dedicated provider.
    /// Comparison is case-insensitive.
    /// </summary>
    bool IsRegistered(string segment);

    /// <summary>All registered prefix names.</summary>
    IReadOnlyCollection<string> AllPrefixes { get; }
}
