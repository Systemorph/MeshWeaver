using MeshWeaver.Data.Completion;

namespace MeshWeaver.Data.Completion;

/// <summary>
/// Default registry that aggregates prefixes from all registered <see cref="IAutocompleteProvider"/>
/// instances by reading their <see cref="IAutocompleteProvider.Prefix"/> property.
/// Each module that registers a specialized provider (Content, Data, etc.) automatically
/// contributes its prefix without additional configuration.
/// </summary>
public class AutocompletePrefixRegistry(IEnumerable<IAutocompleteProvider> providers) : IAutocompletePrefixRegistry
{
    private readonly Lazy<HashSet<string>> _prefixes = new(() =>
        providers
            .Select(p => p.Prefix)
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    public bool IsRegistered(string segment) =>
        !string.IsNullOrEmpty(segment) && _prefixes.Value.Contains(segment);

    public IReadOnlyCollection<string> AllPrefixes => _prefixes.Value;
}
