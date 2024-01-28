using System.Collections.Immutable;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Grouping
{
    public class SelectorPivotGrouper<T, TSelected, TGroup> : IPivotGrouper<T, TGroup>
        where TGroup : class, IGroup, new()
    {
        protected readonly Func<T, int, TSelected> Selector;
        protected string Name;

        public SelectorPivotGrouper(Func<T, TSelected> selector, string name)
        {
            Name = name;
            Selector = (x, _) => selector(x);
        }

        protected SelectorPivotGrouper(Func<T, int, TSelected> selector, string name)
        {
            Selector = selector;
            Name = name;
        }

        public virtual ICollection<PivotGrouping<TGroup, ICollection<T>>> CreateGroupings(ICollection<T> objects, TGroup nullGroup)
        {
            var selectedObjects = objects.Select((x, i) => new { Key = Selector(x, i), Object = x }).ToList();

            var grouped = selectedObjects
                .GroupBy(x => x.Key, x => x.Object);

            var ordered = Order(grouped);

            var nullGroupPrivate = new TGroup
                     {
                         SystemName = nullGroup.SystemName,
                         DisplayName = nullGroup.DisplayName,
                         Coordinates = nullGroup.Coordinates,
                         GrouperName = Name
                     };

            return ordered
                   .Select(x => new PivotGrouping<TGroup, ICollection<T>>(x.Key == null ? nullGroupPrivate : CreateGroupDefinition(x.Key), x.ToArray(), x.Key))
                   .ToArray();
        }

        public virtual void Initialize(IDimensionCache dimensionCache){}

        protected virtual IOrderedEnumerable<IGrouping<TSelected, T>> Order(IEnumerable<IGrouping<TSelected, T>> groups)
        {
            return groups.OrderBy(x => x.Key == null).ThenBy(x => x.Key);
        }

        public virtual IEnumerable<TGroup> Order(IEnumerable<IdentityWithOrderKey<TGroup>> grouped)
        {
            return grouped.OrderBy(x => x.OrderKey == null).ThenBy(x => (TSelected)x.OrderKey).Select(x => x.Identity);
        }

        protected virtual TGroup CreateGroupDefinition(TSelected value)
        {
            var systemName = value.ToString();
            return new TGroup
                   {
                       DisplayName = systemName,
                       SystemName = systemName,
                       GrouperName = Name,
                       Coordinates = ImmutableList<string>.Empty.Add(systemName)
                   };
        }
    }
}