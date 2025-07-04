using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Pivot.Grouping
{
    public class DirectPivotGrouper<T, TGroup> : IPivotGrouper<T, TGroup>
        where TGroup : IGroup, new()
    {
        private readonly Func<IEnumerable<T>, IEnumerable<IGrouping<TGroup, T>>> grouping;

        protected readonly string Name;

        public DirectPivotGrouper(
            Func<IEnumerable<T>, IEnumerable<IGrouping<TGroup, T>>> grouping,
            string name
        )
        {
            this.grouping = grouping;
            Name =
                name
                ?? throw new ArgumentNullException(nameof(name), "Undefined/invalid GrouperName");
        }

        public IReadOnlyCollection<PivotGrouping<TGroup, IReadOnlyCollection<T>>> CreateGroupings(
            IReadOnlyCollection<T> objects,
            TGroup nullGroup)
        {
            var grouped = grouping(objects);
            var ordered = Order(grouped);

            var nullGroupPrivate = new TGroup
            {
                Id = nullGroup.Id,
                DisplayName = nullGroup.DisplayName,
                Coordinates = nullGroup.Coordinates,
                GrouperName = Name
            };
            return ordered
                .Select(x => new PivotGrouping<TGroup, IReadOnlyCollection<T>>(
                    x.Key ?? nullGroupPrivate,
                    x.ToArray(),
                    (object)(x.Key ?? nullGroupPrivate)
                ))
                .ToArray();
        }

        private IOrderedEnumerable<IGrouping<TGroup, T>> Order(
            IEnumerable<IGrouping<TGroup, T>> grouped
        )
        {
            return grouped.OrderBy(x => x.Key == null).ThenBy(x => x.Key?.DisplayName);
        }

        public IEnumerable<TGroup> Order(IEnumerable<IdentityWithOrderKey<TGroup>> grouped)
        {
            return grouped
                .OrderBy(x => x.OrderKey == null)
                .ThenBy(x => x.Identity.DisplayName)
                .Select(x => x.Identity);
        }
    }
}
