using System.Collections.Immutable;
using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Pivot.Grouping
{
    public interface IPivotGrouper<T, TGroup>
        where TGroup : IGroup, new()
    {
        IReadOnlyCollection<PivotGrouping<TGroup?, IReadOnlyCollection<T>>> CreateGroupings(
            IReadOnlyCollection<T> objects,
            TGroup nullGroup);
        IEnumerable<TGroup?> Order(IEnumerable<IdentityWithOrderKey<TGroup>> grouped);
        static readonly TGroup NullGroup =
            new()
            {
                Id = "NullGroup",
                DisplayName = " ",
                GrouperName = "Null",
                Coordinates = ImmutableList<object>.Empty.Add("NullGroup")
            };
        static readonly TGroup TopGroup =
            new()
            {
                Id = "TopGroup",
                DisplayName = "Total",
                GrouperName = "Total",
                Coordinates = ImmutableList<object>.Empty.Add("TopGroup")
            };
        static readonly TGroup TotalGroup =
            new()
            {
                Id = "TotalGroup",
                DisplayName = " ",
                GrouperName = "Aggregate",
                Coordinates = ImmutableList<object>.Empty.Add("TotalGroup")
            };
    }
}
