using System.Collections.Immutable;
using System.Data;
using OpenSmc.Collections;
using OpenSmc.Data;
using OpenSmc.DataCubes;
using OpenSmc.Domain;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Grouping
{
    public class DimensionPivotGrouper<T, TDimension, TGroup>(
        WorkspaceState state,
        Func<T, int, object> selector,
        string Name
    ) : SelectorPivotGrouper<T, object, TGroup>(selector, Name)
        where TGroup : class, IGroup, new()
        where TDimension : class, INamed
    {
        protected WorkspaceState State { get; } = state;
        public DimensionDescriptor DimensionDescriptor { get; }

        public DimensionPivotGrouper(
            WorkspaceState state,
            Func<T, object> selector,
            DimensionDescriptor dimensionDescriptor
        )
            : this(state, (x, _) => selector(x), dimensionDescriptor.SystemName)
        {
            this.DimensionDescriptor = dimensionDescriptor;
        }

        protected override IOrderedEnumerable<IGrouping<object, T>> Order(
            IEnumerable<IGrouping<object, T>> groups
        )
        {
            base.Order(groups);
            if (typeof(IOrdered).IsAssignableFrom(typeof(TDimension)))
                return groups.OrderBy(g => g.Key == null).ThenBy(g => GetOrder(g.Key));
            return groups.OrderBy(g => g.Key == null).ThenBy(g => GetDisplayName(g.Key));
        }

        public override IEnumerable<TGroup> Order(IEnumerable<IdentityWithOrderKey<TGroup>> groups)
        {
            if (typeof(IOrdered).IsAssignableFrom(typeof(TDimension)))
                return groups
                    .OrderBy(x => (string)x.OrderKey == null)
                    .ThenBy(g => GetOrder((string)g.OrderKey))
                    .Select(x => x.Identity);
            return groups
                .OrderBy(x => (string)x.OrderKey == null)
                .ThenBy(g => GetDisplayName((string)g.OrderKey))
                .Select(x => x.Identity);
        }

        protected override TGroup CreateGroupDefinition(object value)
        {
            if (value == null)
                throw new NoNullAllowedException();

            var displayName = GetDisplayName(value);
            return new TGroup
            {
                SystemName = value,
                DisplayName = displayName?.ToString(),
                GrouperName = Id,
                Coordinates = ImmutableList<object>.Empty.Add(value)
            };
        }

        private int GetOrder(object dim)
        {
            if (dim == null)
                // TODO: what to return? (2021/05/22, Roland Buergi)
                return int.MaxValue;
            // ReSharper disable once SuspiciousTypeConversion.Global
            var ordered = State.GetData<TDimension>(dim) as IOrdered;
            return ordered?.Order ?? int.MaxValue;
        }

        private object GetDisplayName(object id)
        {
            if (id == null)
                return IPivotGrouper<T, TGroup>.NullGroup.DisplayName;
            return State.GetData<TDimension>(id)?.DisplayName ?? id;
        }
    }
}
