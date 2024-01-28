using OpenSmc.Scopes.Operations;

namespace OpenSmc.Scopes
{
    public record FilterBuilder
    {
        public FilterBuilder ForScope<TScope>(Func<FilterBuilder<TScope>, FilterBuilder<TScope>> scopeBuilder)
        {
            return this with { Parts = Parts.Concat(scopeBuilder(new FilterBuilder<TScope>()).FilterParts).ToArray() };
        }

        internal IReadOnlyCollection<FilterPart> Parts { get; init; } = Array.Empty<FilterPart>();
    }
}