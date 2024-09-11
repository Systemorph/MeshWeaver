using System.Reflection;
using MeshWeaver.Arithmetics;
using MeshWeaver.Arithmetics.Aggregation;

namespace MeshWeaver.Pivot.Aggregations
{
    public record Aggregations<TTransformed, TIntermediate, TAggregate>
    {
        public Func<IEnumerable<TTransformed>, TIntermediate> Aggregation { get; init; }
        public Func<IEnumerable<TIntermediate>, TIntermediate> AggregationOfAggregates { get; init; }
        public Func<TIntermediate, TAggregate> ResultTransformation { get; init; }
        public string Name { get; init; }

        public Aggregations<TTransformed, TNewIntermediate, TAggregate> WithAggregation<TNewIntermediate>(Func<IEnumerable<TTransformed>, TNewIntermediate> newAggregation)
        {
            return new Aggregations<TTransformed, TNewIntermediate, TAggregate>
                   {
                       Aggregation = newAggregation
                   };
        }

        public Aggregations<TTransformed, TIntermediate, TAggregate> WithAggregationOfAggregates(Func<IEnumerable<TIntermediate>, TIntermediate> newAggregationOfAggregates)
        {
            return this with { AggregationOfAggregates = newAggregationOfAggregates };
        }

        public Aggregations<TTransformed, TIntermediate, TNewAggregate> WithResultTransformation<TNewAggregate>(Func<TIntermediate, TNewAggregate> newResultTransformation)
        {
            return new Aggregations<TTransformed, TIntermediate, TNewAggregate>
                   {
                       Aggregation = Aggregation,
                       AggregationOfAggregates = AggregationOfAggregates,
                       ResultTransformation = newResultTransformation
                   };
        }
    }

    public record Aggregations<TTransformed, TAggregate> : Aggregations<TTransformed, TAggregate, TAggregate>
    {
        public Aggregations()
        {
            ResultTransformation = x => x;
        }
    }

    public static class AggregationsExtensions
    {
        public static TElement Aggregation<TElement>(IEnumerable<TElement> enumerable)
        {
            var list = enumerable.ToList();
            if (typeof(TElement).IsClass)
            {
                if (list.Any(x => x == null))
                    return default;

                var aggregateByProperties = typeof(TElement)
                                            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                            .Where(p => Attribute.IsDefined(p, typeof(AggregateByAttribute))).ToList();
                if (aggregateByProperties.Any())
                    //NB: use reflection because of a generic constraint in AggregateClass
                    return (TElement)typeof(AggregationsExtensions)
                                     .GetMethod(nameof(AggregateClass), BindingFlags.NonPublic | BindingFlags.Static)!
                                     .MakeGenericMethod(typeof(TElement))
                                     .Invoke(null, new object[] { enumerable, aggregateByProperties.Select(p => p.Name).ToArray() });
            }

            return AggregationFunction.Aggregate<TElement, TElement>(list);
        }

        private static TElement AggregateClass<TElement>(IEnumerable<TElement> enumerable, string[] properties)
            where TElement : class
        {
            var aggregated = enumerable.AggregateBy(properties).ToList();
            if (aggregated.Count != 1)
                return default;
            return aggregated[0];
        }

        internal static (TAggregate sum, int count) AverageAggregationOfAggregates<TAggregate>(IEnumerable<(TAggregate sum, int count)> sumsCounts)
        {
            var list = sumsCounts.ToList();
            return (Aggregation(list.Select(s => s.sum)), list.Select(s => s.count).Sum());
        }

        #region Count

        public static Aggregations<TTransformed, int> Count<TTransformed, TAggregate>(this Aggregations<TTransformed, TAggregate, TAggregate> _, Func<TTransformed, bool> predicate = null)
        {
            return new Aggregations<TTransformed, int>
            {
                Name = "Count",
                Aggregation = enumerable => predicate == null
                                                ? enumerable.Count()
                                                : enumerable.Count(predicate),
                AggregationOfAggregates = counts => counts.Sum()
            };
        }

        #endregion

        #region Sum

        public static Aggregations<TAggregate, TAggregate> Sum<TTransformed, TAggregate>(this Aggregations<TTransformed, TAggregate, TAggregate> _)
        {
            return new Aggregations<TAggregate, TAggregate>
            {
                Name = "Sum",
                Aggregation = Aggregation,
                AggregationOfAggregates = Aggregation
            };
        }

        public static Aggregations<TTransformed, double> Sum<TTransformed, TAggregate>(this Aggregations<TTransformed, TAggregate, TAggregate> _, Func<TTransformed, double> selector)
        {
            return new Aggregations<TTransformed, double>
            {
                Name = "Sum",
                Aggregation = enumerable => enumerable.Sum(selector),
                AggregationOfAggregates = sums => sums.Sum(),
            };
        }

        #endregion

        #region Average

        public static Aggregations<TAggregate, (TAggregate sum, int count), TAggregate> Average<TTransformed, TAggregate>(this Aggregations<TTransformed, TAggregate, TAggregate> _, Func<TAggregate, int, TAggregate> average)
        {
            return new Aggregations<TAggregate, (TAggregate sum, int count), TAggregate>
            {
                Name = "Average",
                Aggregation = enumerable =>
                {
                    var list = enumerable.ToList();
                    return (sum: Aggregation(list), count: list.Count);
                },
                AggregationOfAggregates = AverageAggregationOfAggregates,
                ResultTransformation = tuple => average(tuple.sum, tuple.count)
            };
        }

        public static Aggregations<TTransformed, (TNewAggregate sum, int count), TNewAggregate> Average<TTransformed, TAggregate, TNewAggregate>(this Aggregations<TTransformed, TAggregate, TAggregate> _, Func<TTransformed, TNewAggregate> selector, Func<TNewAggregate, int, TNewAggregate> average)
        {
            return new Aggregations<TTransformed, (TNewAggregate sum, int count), TNewAggregate>
            {
                Name = "Average",
                Aggregation = enumerable =>
                {
                    var list = enumerable.Select(selector).ToList();
                    return (Aggregation(list), list.Count);
                },
                AggregationOfAggregates = AverageAggregationOfAggregates,
                ResultTransformation = tuple => average(tuple.sum, tuple.count)
            };
        }

        #endregion

        #region Max

        public static Aggregations<TAggregate, TAggregate> Max<TTransformed, TAggregate>(this Aggregations<TTransformed, TAggregate, TAggregate> _)
        {
            return new Aggregations<TAggregate, TAggregate>
            {
                Name = "Max",
                Aggregation = enumerable => enumerable.Max(),
                AggregationOfAggregates = max => max.Max()
            };
        }

        public static Aggregations<TTransformed, TNewAggregate> Max<TTransformed, TAggregate, TNewAggregate>(this Aggregations<TTransformed, TAggregate, TAggregate> _, Func<TTransformed, TNewAggregate> selector)
        {
            return new Aggregations<TTransformed, TNewAggregate>
            {
                Name = "Max",
                Aggregation = enumerable => enumerable.Max(selector),
                AggregationOfAggregates = max => max.Max()
            };
        }

        #endregion

        #region Min

        public static Aggregations<TAggregate, TAggregate> Min<TTransformed, TAggregate>(this Aggregations<TTransformed, TAggregate, TAggregate> _)
        {
            return new Aggregations<TAggregate, TAggregate>
            {
                Name = "Min",
                Aggregation = enumerable => enumerable.Min(),
                AggregationOfAggregates = min => min.Min()
            };
        }

        public static Aggregations<TTransformed, TNewAggregate> Min<TTransformed, TAggregate, TNewAggregate>(this Aggregations<TTransformed, TAggregate, TAggregate> _, Func<TTransformed, TNewAggregate> selector)
        {
            return new Aggregations<TTransformed, TNewAggregate>
            {
                Name = "Min",
                Aggregation = enumerable => enumerable.Min(selector),
                AggregationOfAggregates = min => min.Min()
            };
        }

        #endregion
    }
}
