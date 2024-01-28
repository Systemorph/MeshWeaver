using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Grouping
{
    public class AutomaticEnumerationPivotGrouper<T, TGroup> : SelectorPivotGrouper<T, int, TGroup>
        where TGroup : class, IGroup, new()
    {
        public AutomaticEnumerationPivotGrouper()
            : base((_, i) => i + 1, PivotConst.AutomaticEnumerationPivotGrouperName)
        {
        }

        public override ICollection<PivotGrouping<TGroup, ICollection<T>>> CreateGroupings(ICollection<T> objects, TGroup nullGroup)
        {
            if (objects.Count == 1)
                return new PivotGrouping<TGroup, ICollection<T>>[] { new(IPivotGrouper<T, TGroup>.TopGroup, objects, 0) };
            return base.CreateGroupings(objects, nullGroup);
        }
    }
}