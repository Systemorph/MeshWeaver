using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Pivot.Grouping
{
    public class AutomaticEnumerationPivotGrouper<T, TGroup>()
        : SelectorPivotGrouper<T, int, TGroup>(PivotConst.AutomaticEnumerationPivotGrouperName, (_, i) => i + 1)
        where TGroup : class, IGroup, new()
    {
        public override IReadOnlyCollection<PivotGrouping<TGroup?, IReadOnlyCollection<T?>>> CreateGroupings(IReadOnlyCollection<T?> objects, TGroup nullGroup)
        {
            if (objects.Count == 1)
                return
                [
                    new(IPivotGrouper<T, TGroup>.TopGroup, objects, 0)
                ];
            return base.CreateGroupings(objects, nullGroup);
        }
    }
}
