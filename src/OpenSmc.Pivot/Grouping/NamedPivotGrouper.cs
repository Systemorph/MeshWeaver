using System.Collections.Immutable;
using OpenSmc.Domain.Abstractions;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Grouping
{
    public class NamedPivotGrouper<T, TGroup> : DirectPivotGrouper<T, TGroup>
        where TGroup : class, IGroup, new()
        where T : INamed
    {
        public NamedPivotGrouper(string name)
            : base(x => x.GroupBy(o => new TGroup
                                       {
                                           SystemName = o.SystemName,
                                           DisplayName = o.DisplayName,
                                           GrouperName = name,
                                           Coordinates = ImmutableList<string>.Empty.Add(o.SystemName)
                                       }), name)
        {
        }
    }
}