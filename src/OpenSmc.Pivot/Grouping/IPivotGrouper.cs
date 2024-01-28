using System.Collections.Immutable;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Grouping
{
    public interface IPivotGrouper<T, TGroup>
        where TGroup : IGroup, new()
    {
        ICollection<PivotGrouping<TGroup, ICollection<T>>> CreateGroupings(ICollection<T> objects, TGroup nullGroup);
        IEnumerable<TGroup> Order(IEnumerable<IdentityWithOrderKey<TGroup>> grouped);
        void Initialize(IDimensionCache dimensionCache);
        static readonly TGroup NullGroup = new() { SystemName = "NullGroup", DisplayName = " ", GrouperName = "Null", Coordinates = ImmutableList<string>.Empty.Add("NullGroup") };
        static readonly TGroup TopGroup = new() { SystemName = "TopGroup", DisplayName = "Total", GrouperName = "Total", Coordinates = ImmutableList<string>.Empty.Add("TopGroup") };
        static readonly TGroup TotalGroup = new() { SystemName = "TotalGroup", DisplayName = " ", GrouperName = "Aggregate", Coordinates = ImmutableList<string>.Empty.Add("TotalGroup") };
    }
}