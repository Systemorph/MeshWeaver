using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Data.Completion;

/// <summary>
/// Default registry that aggregates prefixes from all registered <see cref="IAutocompleteProvider"/>
/// instances by reading their <see cref="IAutocompleteProvider.Prefix"/> property.
///
/// Uses <see cref="IServiceProvider"/> for lazy provider resolution to avoid circular DI
/// (generic providers like MeshNodeAutocompleteProvider inject this registry, but they are
/// also <see cref="IAutocompleteProvider"/> instances themselves).
/// </summary>
public class AutocompletePrefixRegistry(IServiceProvider serviceProvider) : IAutocompletePrefixRegistry
{
    private HashSet<string>? _prefixes;
    private readonly object _lock = new();

    private HashSet<string> Prefixes
    {
        get
        {
            if (_prefixes != null) return _prefixes;
            lock (_lock)
            {
                if (_prefixes != null) return _prefixes;
                var providers = serviceProvider.GetServices<IAutocompleteProvider>();
                _prefixes = providers
                    .Select(p => p.Prefix)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Cast<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return _prefixes;
            }
        }
    }

    public bool IsRegistered(string segment) =>
        !string.IsNullOrEmpty(segment) && Prefixes.Contains(segment);

    public IReadOnlyCollection<string> AllPrefixes => Prefixes;
}
