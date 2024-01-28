using OpenSmc.Pivot.Grouping;
using OpenSmc.Pivot.Models.Interfaces;

namespace OpenSmc.Pivot.Processors
{
    public class HierarchicalRowGroupAggregator<TIntermediate, TAggregate, TGroup>
        where TGroup : IGroup, new()
    {
        private readonly IDictionary<string, HierarchicalRowGroupAggregator<TIntermediate, TAggregate, TGroup>> subAggregates;
        public ICollection<PivotGrouping<TGroup, TIntermediate>> AggregatedGroupings { get; }

        // TODO V10: add aggregated grouping here as a property Agg of agg (2022/06/14, Ekaterina Mishina)

        public HierarchicalRowGroupAggregator(ICollection<PivotGrouping<TGroup, TIntermediate>> aggregatedGroupings, IDictionary<TGroup, HierarchicalRowGroupAggregator<TIntermediate, TAggregate, TGroup>> subAggregates)
        {
            this.subAggregates = subAggregates?.ToDictionary(x => x.Key.SystemName, x => x.Value);
            //this.subAggregates.Add("Agg",);
            AggregatedGroupings = aggregatedGroupings;
            foreach (var agg in AggregatedGroupings)
            {
                if (this.subAggregates != null && this.subAggregates.TryGetValue(agg.Identity.Coordinates.Last(), out var aggregator))
                    aggregator.Total = agg;

            }
        }

        public PivotGrouping<TGroup, TIntermediate> Total { get; set; }

        public IList<object> Transform<TValue>(ICollection<Func<TAggregate, TValue>> valueSelectors, Func<TIntermediate, TAggregate> resultTransformation)
        {
            if (AggregatedGroupings.Count == 0)
                return null;
            // TODO V10: get rid of null subAggregates and simplify this if (2022/03/31, Ekaterina Mishina)
            if (subAggregates == null || subAggregates.Count == 0) // lowest level
            {
                // TODO V10: check the case of a single group which is not Total (2022/03/31, Ekaterina Mishina)
                if (AggregatedGroupings.Count == 1 && AggregatedGroupings.First().Identity.SystemName == IPivotGrouper<TValue, TGroup>.TopGroup.SystemName)
                    return valueSelectors.Select(s => (object)s(resultTransformation(AggregatedGroupings.First().Object))).ToArray();

                return valueSelectors.Select(vs =>
                                             {
                                                 var dictionary = AggregatedGroupings
                                                     .ToDictionary(a => a.Identity.Coordinates.Last(), a => Equals(a.Object, default(TAggregate)) ? default : vs(resultTransformation(a.Object)));
                                                 if (Total != null)
                                                 {
                                                     var total = Equals(Total.Object, default(TAggregate)) ? default : vs(resultTransformation(Total.Object));
                                                     dictionary.Add(IPivotGrouper<TValue,TGroup>.TotalGroup.SystemName, total);
                                                 }
                                                 return dictionary;
                                             })
                                     .Cast<object>()
                                     .ToArray();
            }

            var subAggregatesByValueSelector = subAggregates?.ToDictionary(x => x.Key, x => x.Value.Transform(valueSelectors, resultTransformation));
            
            var ret = valueSelectors.Select((vs, i) =>
                                            {
                                                var dictionary = AggregatedGroupings.Select(a =>
                                                                                            {
                                                                                                object subObject;

                                                                                                if (subAggregatesByValueSelector.TryGetValue(a.Identity.Coordinates.Last(), out var subObjects))
                                                                                                    subObject = subObjects[i];
                                                                                                else //how can we end up here?!
                                                                                                    subObject = Equals(a.Object, default(TAggregate)) ? default : vs(resultTransformation(a.Object));

                                                                                                return new
                                                                                                       {
                                                                                                           a.Identity,
                                                                                                           SubObject = subObject
                                                                                                       };
                                                                                                
                                                                                            })
                                                                                    .ToDictionary(x => x.Identity.Coordinates.Last(), x => x.SubObject);
                                                if (Total != null)
                                                {
                                                    var total = Equals(Total.Object, default(TAggregate)) ? default : vs(resultTransformation(Total.Object));
                                                    dictionary.Add(IPivotGrouper<TValue,TGroup>.TotalGroup.SystemName, total);
                                                }

                                                return dictionary;
                                            })
                                          .Cast<object>()
                                          .ToArray();
            return ret;
        }
    }
}