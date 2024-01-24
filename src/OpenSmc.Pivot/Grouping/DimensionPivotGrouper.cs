using System.Collections.Immutable;
using System.Data;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Grouping
{
    public class DimensionPivotGrouper<T, TDimension, TGroup> : SelectorPivotGrouper<T, string, TGroup>
        where TGroup : class, IGroup, new()
        where TDimension : class, INamed
    {
        protected IDimensionCache DimensionCache;
        protected readonly DimensionDescriptor DimensionDescriptor;

        public DimensionPivotGrouper(Func<T, string> selector, DimensionDescriptor dimensionDescriptor)
            : base(selector, dimensionDescriptor.SystemName)
        {
            DimensionDescriptor = dimensionDescriptor;
        }

        protected DimensionPivotGrouper(Func<T, int, string> selector, DimensionDescriptor dimensionDescriptor)
            :base(selector, dimensionDescriptor.SystemName)
        {
            DimensionDescriptor = dimensionDescriptor;
        }

        protected override IOrderedEnumerable<IGrouping<string, T>> Order(IEnumerable<IGrouping<string, T>> groups)
        {
            if (typeof(IOrdered).IsAssignableFrom(typeof(TDimension)))
                return groups.OrderBy(g => g.Key == null).ThenBy(g => GetOrder(g.Key));
            return groups.OrderBy(g => g.Key == null).ThenBy(g => GetDisplayName(g.Key));
        }

        public override IEnumerable<TGroup> Order(IEnumerable<IdentityWithOrderKey<TGroup>> groups)
        {
            if (typeof(IOrdered).IsAssignableFrom(typeof(TDimension)))
                return groups.OrderBy(x => (string)x.OrderKey == null).ThenBy(g => GetOrder((string)g.OrderKey)).Select(x => x.Identity);
            return groups.OrderBy(x => (string)x.OrderKey == null).ThenBy(g => GetDisplayName((string)g.OrderKey)).Select(x => x.Identity);
        }
        
        protected override TGroup CreateGroupDefinition(string value)
        {
            if (value == null)
                throw new NoNullAllowedException();

            var displayName = GetDisplayName(value);
            return new TGroup
                   {
                       SystemName = value,
                       DisplayName = displayName,
                       GrouperName = Name,
                       Coordinates = ImmutableList<string>.Empty.Add(value)
                   };
        }

        public override void Initialize(IDimensionCache cache)
        {
            DimensionCache = cache;
            if (DimensionCache == null)
                return;

            DimensionCache.Initialize(DimensionDescriptor.RepeatOnce());
        }

        private int GetOrder(string dim)
        {
            if (dim == null)
                // TODO: what to return? (2021/05/22, Roland Buergi)
                return int.MaxValue;
            // ReSharper disable once SuspiciousTypeConversion.Global
            var ordered = DimensionCache?.Get<TDimension>(dim) as IOrdered;
            return ordered?.Order ?? int.MaxValue;
        }

        private string GetDisplayName(string dim)
        {
            if (dim == null)
                return IPivotGrouper<T, TGroup>.NullGroup.DisplayName;
            return DimensionCache?.Get<TDimension>(dim)?.DisplayName ?? dim;
        }
    }
}