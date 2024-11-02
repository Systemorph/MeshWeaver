﻿using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using MeshWeaver.Pivot.Models.Interfaces;
using MeshWeaver.Utils;

namespace MeshWeaver.Pivot.Grouping
{
    public class PropertyPivotGrouper<T, TGroup> : SelectorPivotGrouper<T, PropertyInfo, TGroup>
        where TGroup : class, IGroup, new()
    {
        public PropertyPivotGrouper(Func<T, PropertyInfo> selector)
            : base(PivotConst.PropertyPivotGrouperName, selector) { }

        protected override IOrderedEnumerable<IGrouping<PropertyInfo, T>> Order(
            IEnumerable<IGrouping<PropertyInfo, T>> groups
        )
        {
            return groups.OrderBy(x =>
                x.Key?.GetCustomAttribute<DisplayAttribute>()?.GetOrder() ?? int.MaxValue
            );
        }

        public override IEnumerable<TGroup> Order(IEnumerable<IdentityWithOrderKey<TGroup>> grouped)
        {
            return grouped
                .OrderBy(x =>
                    ((PropertyInfo)x.OrderKey)?.GetCustomAttribute<DisplayAttribute>()?.GetOrder()
                    ?? int.MaxValue
                )
                .Select(x => x.Identity);
        }

        protected override TGroup CreateGroupDefinition(PropertyInfo value)
        {
            var displayName =
                value.GetCustomAttribute<DisplayAttribute>()?.Name ?? value.Name.Wordify();
            var systemName = value.Name;
            return new TGroup
            {
                Id = systemName,
                DisplayName = displayName,
                GrouperName = Id,
                Coordinates = ImmutableList<object>.Empty.Add(systemName)
            };
        }
    }
}
