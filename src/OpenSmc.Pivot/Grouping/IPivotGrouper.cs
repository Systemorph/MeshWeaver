using System.Collections.Immutable;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Grouping
{
    public interface IPivotGrouper<T, TGroup>
        where TGroup : IGroup, new()
    {
        IReadOnlyCollection<PivotGrouping<TGroup, IReadOnlyCollection<T>>> CreateGroupings(
            IReadOnlyCollection<T> objects,
            TGroup nullGroup
        );
        IEnumerable<TGroup> Order(IEnumerable<IdentityWithOrderKey<TGroup>> grouped);
        static readonly TGroup NullGroup =
            new()
            {
                Id = "NullGroup",
                DisplayName = " ",
                GrouperId = "Null",
                Coordinates = ImmutableList<object>.Empty.Add("NullGroup")
            };
        static readonly TGroup TopGroup =
            new()
            {
                Id = "TopGroup",
                DisplayName = "Total",
                GrouperId = "Total",
                Coordinates = ImmutableList<object>.Empty.Add("TopGroup")
            };
        static readonly TGroup TotalGroup =
            new()
            {
                Id = "TotalGroup",
                DisplayName = " ",
                GrouperId = "Aggregate",
                Coordinates = ImmutableList<object>.Empty.Add("TotalGroup")
            };
    }
}
