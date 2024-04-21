using System.Collections.Immutable;
using OpenSmc.Data;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Grouping
{
    public class SelectorPivotGrouper<T, TSelected, TGroup>(
        Func<T, int, TSelected> selector,
        object id
    ) : IPivotGrouper<T, TGroup>
        where TGroup : class, IGroup, new()
    {
        protected Func<T, int, TSelected> Selector { get; } = selector;
        public object Id { get; } = id;

        public SelectorPivotGrouper(Func<T, TSelected> selector, object id)
            : this((x, _) => selector.Invoke(x), id) { }

        public virtual IReadOnlyCollection<
            PivotGrouping<TGroup, IReadOnlyCollection<T>>
        > CreateGroupings(IReadOnlyCollection<T> objects, TGroup nullGroup)
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
                GrouperId = Id
            };

            return ordered
                .Select(x => new PivotGrouping<TGroup, IReadOnlyCollection<T>>(
                    x.Key == null ? nullGroupPrivate : CreateGroupDefinition(x.Key),
                    x.ToArray(),
                    x.Key
                ))
                .ToArray();
        }

        protected virtual IOrderedEnumerable<IGrouping<TSelected, T>> Order(
            IEnumerable<IGrouping<TSelected, T>> groups
        )
        {
            return groups.OrderBy(x => x.Key == null).ThenBy(x => x.Key);
        }

        public virtual IEnumerable<TGroup> Order(IEnumerable<IdentityWithOrderKey<TGroup>> grouped)
        {
            return grouped
                .OrderBy(x => x.OrderKey == null)
                .ThenBy(x => (TSelected)x.OrderKey)
                .Select(x => x.Identity);
        }

        protected virtual TGroup CreateGroupDefinition(TSelected value)
        {
            var systemName = value.ToString();
            return new TGroup
            {
                DisplayName = systemName,
                Id = systemName,
                GrouperId = Id,
                Coordinates = ImmutableList<object>.Empty.Add(systemName)
            };
        }
    }
}
