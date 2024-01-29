using OpenSmc.DataCubes;
using OpenSmc.Scopes;

namespace OpenSmc.TestDomain.Scopes
{
    public interface IFilterableTestScope : IScope<GuidIdentity, IdentitiesStorage>
    {
        // TODO: This can probably be simplified, looks clumsy. (2021/05/02, Roland Buergi)
        public static FilterBuilder FilterConfig(FilterBuilder builder) =>
            builder.ForScope<IFilterableTestScope>(scope =>
                                                       scope.ForMember(x => x.FilteredNumbers,
                                                                       part => part.WithName("Even")
                                                                                   .Filter<bool>((elements, isEven) => elements.Where(i => i % 2 == (isEven ? 0 : 1)).ToArray())));

        int[] Numbers => Enumerable.Range(1, 10).ToArray();
        int[] FilteredNumbers => Numbers;
        double FilteredSum => FilteredNumbers.Sum();
    }

    public interface IFilterableTestScopeWithExplicitInterface : IFilterableTestScope, IFilterable
    {
    }
}