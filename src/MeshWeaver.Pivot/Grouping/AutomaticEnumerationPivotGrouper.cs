﻿using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Pivot.Grouping
{
    public class AutomaticEnumerationPivotGrouper<T, TGroup> : SelectorPivotGrouper<T, int, TGroup>
        where TGroup : class, IGroup, new()
    {
        public AutomaticEnumerationPivotGrouper()
            : base((_, i) => i + 1, PivotConst.AutomaticEnumerationPivotGrouperName) { }

        public override IReadOnlyCollection<
            PivotGrouping<TGroup, IReadOnlyCollection<T>>
        > CreateGroupings(IReadOnlyCollection<T> objects, TGroup nullGroup)
        {
            if (objects.Count == 1)
                return new PivotGrouping<TGroup, IReadOnlyCollection<T>>[]
                {
                    new(IPivotGrouper<T, TGroup>.TopGroup, objects, 0)
                };
            return base.CreateGroupings(objects, nullGroup);
        }
    }
}
