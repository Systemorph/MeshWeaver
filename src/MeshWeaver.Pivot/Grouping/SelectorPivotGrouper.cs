using System.Collections.Immutable;
using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Pivot.Grouping
{
    public class SelectorPivotGrouper<T, TSelected, TGroup>(
        string id,
        Func<T, int, TSelected?> selector)
        : IPivotGrouper<T, TGroup>
        where TGroup : class, IGroup, new()
    {
        protected Func<T, int, TSelected?> Selector { get; } = selector;

        protected string Id { get; } = id;
        public SelectorPivotGrouper(string id, Func<T, TSelected?> selector)
            : this(id, (x, _) => selector.Invoke(x)) { }

        public virtual IReadOnlyCollection<PivotGrouping<TGroup?, IReadOnlyCollection<T>>> CreateGroupings(IReadOnlyCollection<T> objects, TGroup nullGroup)
        {

            var selectedObjects = objects
                .Select((x, i) => new { Key = Selector(x, i), Object = x })
                .ToList();

            var grouped = selectedObjects.GroupBy(x => x.Key, x => x.Object);

            var ordered = Order(grouped);

            var nullGroupPrivate = new TGroup
            {
                Id = nullGroup.Id,
                DisplayName = nullGroup.DisplayName,
                Coordinates = nullGroup.Coordinates,
                GrouperName = Id
            };

            return ordered
                .Select(x => new PivotGrouping<TGroup?, IReadOnlyCollection<T>>(
                    x.Key == null ? nullGroupPrivate : CreateGroupDefinition(x.Key),
                    x.ToArray(),
                    x.Key == null ? nullGroupPrivate.Id! : x.Key
                ))
                .ToArray();
        }

        protected virtual IOrderedEnumerable<IGrouping<TSelected?, T>> Order(
            IEnumerable<IGrouping<TSelected?, T>> groups
        )
        {
            return groups.OrderBy(x => x.Key == null).ThenBy(x => x.Key?.ToString());
        }

        public virtual IEnumerable<TGroup> Order(IEnumerable<IdentityWithOrderKey<TGroup>> grouped)
        {
            return grouped
                .OrderBy(x => x.OrderKey == null)
                .ThenBy(x => x.OrderKey?.ToString())
                .Select(x => x.Identity);
        }

        protected virtual TGroup CreateGroupDefinition(TSelected value)
        {
            var id = value;
            return new TGroup
            {
                DisplayName = id?.ToString() ?? "",
                Id = id!,
                GrouperName = Id,
                Coordinates = ImmutableList<object>.Empty.Add(id!)
            };
        }
    }
}
