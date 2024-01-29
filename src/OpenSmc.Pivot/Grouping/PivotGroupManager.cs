﻿using System.Collections.Immutable;
using OpenSmc.Collections;
using OpenSmc.Pivot.Aggregations;
using OpenSmc.Pivot.Models;
using OpenSmc.Pivot.Models.Interfaces;
using OpenSmc.Pivot.Processors;
using static System.String;

namespace OpenSmc.Pivot.Grouping
{
    public class PivotGroupManager<T, TIntermediate, TAggregate, TGroup>
        where TGroup : IGroup, IItemWithCoordinates, new()
    {
        protected readonly PivotGroupManager<T, TIntermediate, TAggregate, TGroup> SubGroup;
        protected readonly IPivotGrouper<T, TGroup> Grouper;
        private readonly Aggregations<T, TIntermediate, TAggregate> aggregationFunctions;
        private readonly Dictionary<string, HashSet<IdentityWithOrderKey<TGroup>>> groups = new();

        public PivotGroupManager(IPivotGrouper<T, TGroup> grouper, PivotGroupManager<T, TIntermediate, TAggregate, TGroup> subGroup, Aggregations<T, TIntermediate, TAggregate> aggregationFunctions)
        {
            Grouper = grouper;
            SubGroup = subGroup;
            this.aggregationFunctions = aggregationFunctions;
        }

        public IList<ColumnGroup> GetColumnGroups(IReadOnlyCollection<Column> valueColumns)
        {
            if (groups.Count == 0)
                return new List<ColumnGroup>();

            if (!groups.TryGetValue(IPivotGrouper<T, TGroup>.TopGroup.SystemName, out var topGroups))
                throw new Exception("Only for top column group manager");
            
            var orderedGroups = Grouper.Order(topGroups);

            // TODO V10: if we want a summary group on top, we need one more top column group (2022/01/13, Ekaterina Mishina)
            var columnGroups = orderedGroups.Where(x => x.SystemName != IPivotGrouper<T, TGroup>.TopGroup.SystemName)
                                            .Select(x => new ColumnGroup(x))
                                            .ToList();

            var columnGroupsWithChildren = AddChildren(columnGroups, valueColumns);

            return columnGroupsWithChildren;
        }

        private IList<ColumnGroup> AddChildren(IEnumerable<ColumnGroup> columnGroups, IReadOnlyCollection<Column> valueColumns)
        {
            var subGroupsByParent = SubGroup?.groups;

            return columnGroups.Select(group =>
                                       {
                                           var groupCoordinates = group.Coordinates;

                                           if (subGroupsByParent != null && subGroupsByParent.TryGetValue(group.SystemName, out var children))
                                           {
                                               var orderedChildren = SubGroup.Grouper.Order(children);
                                               var childrenWithChildren = SubGroup.AddChildren(orderedChildren.Select(x => new ColumnGroup(x)), valueColumns);
                                               if (childrenWithChildren.Count > 1)
                                               {
                                                   var totalGroupCoordinates = groupCoordinates.Append(IPivotGrouper<T, TGroup>.TotalGroup.SystemName).ToList();
                                                   var totalGroup = new ColumnGroup
                                                                    {
                                                                        SystemName = Join(".", totalGroupCoordinates),
                                                                        DisplayName = " ",
                                                                        GrouperName = group.GrouperName,
                                                                        Coordinates = totalGroupCoordinates.ToImmutableList(),
                                                                    };
                                                   var totalValues = valueColumns.Select(c =>
                                                                                         {
                                                                                             var cc = totalGroupCoordinates.Append(c.SystemName).ToList();
                                                                                             return c with
                                                                                                    {
                                                                                                        SystemName = Join(".", cc),
                                                                                                        Coordinates = cc.ToImmutableList(),
                                                                                                    };
                                                                                         });
                                                   totalGroup = totalGroup.AddChildren(totalValues);
                                                   childrenWithChildren.Add(totalGroup);
                                               }

                                               return group.AddChildren(childrenWithChildren);
                                           }

                                           var leafColumns = valueColumns.Select(c =>
                                                                                 {
                                                                                     var coordinates = groupCoordinates.Append(c.SystemName).ToList();
                                                                                     return c with
                                                                                            {
                                                                                                SystemName = Join(".", coordinates),
                                                                                                Coordinates = coordinates.ToImmutableList(),
                                                                                            };
                                                                                 });
                                           return group.AddChildren(leafColumns);
                                       })
                               .ToList();
        }

        public IEnumerable<PivotGrouping<TGroup, ICollection<T>>> CreateRowGroupings(ICollection<T> objects, IReadOnlyCollection<string> parentCoordinates)
        {
            if (objects.Count == 0)
                return Enumerable.Empty<PivotGrouping<TGroup, ICollection<T>>>();
            var myGrouping = GetMyGroups(objects);

            if (SubGroup != null)
                return MixRowGroupsWithSubGroups(myGrouping, parentCoordinates);

            return GetFinalizedReportGroupings(myGrouping, parentCoordinates);
        }


        /// <summary>
        /// This needs to be abstract as in C# 9 there is no generic constraint for record types available ==> cannot use with operator.
        /// </summary>
        /// <param name="original"></param>
        /// <param name="fullCoordinatesBySystemName"></param>
        /// <returns></returns>
        protected virtual TGroup GetModifiedGroup(TGroup original, IDictionary<string, string[]> fullCoordinatesBySystemName)
        {
            if (original.SystemName == IPivotGrouper<T, TGroup>.TopGroup.SystemName)
                return original;

            var fullCoordinates = fullCoordinatesBySystemName[original.SystemName];

            if (original is RowGroup rg)
                return (TGroup)(object)(rg with
                       {
                           SystemName = Join(".", fullCoordinates),
                           Coordinates = fullCoordinates.ToImmutableList()
                       });

            if (original is ColumnGroup cg)
                return (TGroup)(object)(cg with
                                        {
                                            SystemName = Join(".", fullCoordinates),
                                            Coordinates = fullCoordinates.ToImmutableList()
                                        });

            throw new ArgumentException($"Unknown grouping type {typeof(TGroup)}");
        }

        private IEnumerable<PivotGrouping<TGroup, ICollection<T>>> MixRowGroupsWithSubGroups(ICollection<PivotGrouping<TGroup, ICollection<T>>> groupings, IReadOnlyCollection<string> parentCoordinates)
        {
            Dictionary<string, IEnumerable<PivotGrouping<TGroup, ICollection<T>>>> subGroupsByParent = new();
            foreach (var g in groupings)
            {
                var fullGroupCoordinates = !parentCoordinates.Any() ? g.Identity.Coordinates : parentCoordinates.Concat(g.Identity.Coordinates).ToImmutableList();
                var subGroups = SubGroup.CreateRowGroupings(g.Object, fullGroupCoordinates).ToArray();
                if (subGroups.Length == 0)
                    continue;
                subGroupsByParent[fullGroupCoordinates.Any() ? Join(".", fullGroupCoordinates) : IPivotGrouper<T, TGroup>.TopGroup.SystemName] = subGroups;
            }

            var modified = GetFinalizedReportGroupings(groupings, parentCoordinates);
            foreach (var g in modified)
            {
                yield return g;
                if (subGroupsByParent.TryGetValue(Join(".", g.Identity.Coordinates), out var sg))
                    foreach (var el in sg)
                        yield return el;
            }
        }

        private ICollection<PivotGrouping<TGroup, ICollection<T>>> GetFinalizedReportGroupings(ICollection<PivotGrouping<TGroup, ICollection<T>>> groupings, IReadOnlyCollection<string> parentCoordinates)
        {
            IDictionary<string, string[]> fullCoordinatesBySystemName = GetFullGroupingsCoordinates(groupings, parentCoordinates);

            var ret = groupings.Select(g => new PivotGrouping<TGroup, ICollection<T>>(GetModifiedGroup(g.Identity, fullCoordinatesBySystemName), g.Object, g.OrderKey)).ToArray();
            foreach (var g in ret)
            {
                var key = parentCoordinates.Any() ? Join(".", parentCoordinates) : IPivotGrouper<T, TGroup>.TopGroup.SystemName;
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new HashSet<IdentityWithOrderKey<TGroup>>();
                    groups.Add(key, list);
                }

                list.Add(g.IdentityWithOrderKey);
            }

            return ret;
        }

        private IDictionary<string, string []> GetFullGroupingsCoordinates(ICollection<PivotGrouping<TGroup, ICollection<T>>> groupings, IReadOnlyCollection<string> parentCoordinates)
        {
            if (parentCoordinates == null)
                return groupings.Select(x => x.Identity?.SystemName)
                                .Where(x => x != null)
                                .ToDictionary(x => x, x => new[] { x });

            return groupings
                   .Select(x => x.Identity?.SystemName)
                   .Where(x => x != null)
                   .ToDictionary(x => x, x => parentCoordinates.Concat(x.RepeatOnce()).ToArray());
        }

        private ICollection<PivotGrouping<TGroup, ICollection<T>>> GetMyGroups(ICollection<T> objects)
        {
            var nullGroup = typeof(TGroup).IsAssignableFrom(typeof(RowGroup))
                            && Grouper.GetType().IsAssignableFrom(typeof(PropertyPivotGrouper<T, RowGroup>))
                                ? IPivotGrouper<T, TGroup>.TopGroup
                                : IPivotGrouper<T, TGroup>.NullGroup;
            var ret = Grouper.CreateGroupings(objects, nullGroup);
            return ret;
        }

        public HierarchicalRowGroupAggregator<TIntermediate, TAggregate, TGroup> GetAggregates(ICollection<T> objects, IReadOnlyCollection<string> parentCoordinates)
        {
            // if lowest level, get aggregates from object
            if (SubGroup == null)
                return new HierarchicalRowGroupAggregator<TIntermediate, TAggregate, TGroup>(GetFinalizedReportGroupings(GetMyGroups(objects), parentCoordinates)
                                                                                   .Select(g => new PivotGrouping<TGroup, TIntermediate>(g.Identity, aggregationFunctions.Aggregation(g.Object), g.OrderKey))
                                                                                   .ToArray(), 
                                                                                             default);

            // otherwise aggregate recursively upper levels
            var myGrouping = GetMyGroups(objects);

            var subAggregates = myGrouping.Select(g =>
                                                  {
                                                      var sa = SubGroup.GetAggregates(g.Object, parentCoordinates.Concat(g.Identity.Coordinates).ToArray());
                                                      return new
                                                             {
                                                                 g.Identity,
                                                                 SubAggregates = sa,
                                                                 Totals = sa?.AggregatedGroupings.Count > 0
                                                                              ? aggregationFunctions.AggregationOfAggregates(sa.AggregatedGroupings.Select(y => y.Object))
                                                                              : aggregationFunctions.Aggregation(g.Object) // lowest level for this grouping
                                                             };
                                                  }).ToArray();

            var finalizedReportGroupings = GetFinalizedReportGroupings(myGrouping, parentCoordinates);
            var totals = finalizedReportGroupings
                         .Join(subAggregates,
                               x => x.Identity?.Coordinates.Last(),
                               x => x.Identity?.SystemName,
                               (m, sa) => new PivotGrouping<TGroup, TIntermediate>(m.Identity, sa.Totals, m.OrderKey))
                         .ToArray();

            return new HierarchicalRowGroupAggregator<TIntermediate, TAggregate, TGroup>(totals,
                                                                                         subAggregates
                                                                                             .Where(x => x.SubAggregates?.AggregatedGroupings.Count > 0)
                                                                                             .ToDictionary(x => x.Identity, x => x.SubAggregates));
        }
    }
}