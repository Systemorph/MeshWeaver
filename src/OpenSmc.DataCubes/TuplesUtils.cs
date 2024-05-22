using System.Reflection;
using AspectCore.Extensions.Reflection;
using OpenSmc.Domain;
using CustomAttributeExtensions = System.Reflection.CustomAttributeExtensions;

namespace OpenSmc.DataCubes
{
    public static class TuplesUtils<T>
    {
        private class PropertyToDimensionDescriptor
        {
            public PropertyToDimensionDescriptor(
                PropertyInfo property,
                PropertyReflector reflector,
                DimensionDescriptor descriptor
            )
            {
                Property = property;
                Reflector = reflector;
                Descriptor = descriptor;
            }

            public PropertyInfo Property { get; }
            public PropertyReflector Reflector { get; }
            public DimensionDescriptor Descriptor { get; }
        }

        private static readonly Dictionary<
            string,
            PropertyToDimensionDescriptor
        > PropertiesByDimension = typeof(T)
            .GetProperties()
            .Select(p => new
            {
                Property = p,
                DimensionAttribute = CustomAttributeExtensions.GetCustomAttribute<DimensionAttribute>(
                    (MemberInfo)p
                )
            })
            .Where(p => p.DimensionAttribute != null)
            .Select(p => new PropertyToDimensionDescriptor(
                p.Property,
                p.Property.GetReflector(),
                new DimensionDescriptor(p.DimensionAttribute.Name, p.DimensionAttribute.Type)
            ))
            .ToDictionaryValidated(x => x.Descriptor.SystemName);

        public static PropertyReflector GetReflector(string dimension)
        {
            PropertiesByDimension.TryGetValue(dimension, out var ret);
            return ret?.Reflector;
        }

        public static IEnumerable<DataSlice<T>> GetDimensionTuples(
            string[] dimensions,
            IEnumerable<T> data
        )
        {
            var properties = dimensions.Select(d => new
            {
                Dimension = d,
                Property = PropertiesByDimension.TryGetValue(d, out var p) ? p : null
            });

            var slices = data.Select(d => new DataSlice<T>(
                d,
                new DimensionTuple(
                    properties.Select(p =>
                        (dimension: p.Dimension, value: p.Property?.Reflector.GetValue(d))
                    )
                )
            ));

            return slices;
        }

        public static Func<T, bool> GetFilter(
            IEnumerable<(string dimension, object value)> tupleFilter
        )
        {
            var filters = tupleFilter.Select(CreateComparer).Where(x => x != default).ToArray();
            if (filters.Length == 0)
                return null;
            return CombineFilters(filters);
        }

        private static Func<T, bool> CombineFilters(Func<T, bool>[] filters)
        {
            bool Combined(T o) => filters.Aggregate(true, (x, y) => x && y(o));
            return Combined;
        }

        private static Func<T, bool> CreateComparer((string dimension, object value) valueTuple)
        {
            var dimensionReflector = GetReflector(valueTuple.dimension);
            if (dimensionReflector == null)
                return null;

            if (valueTuple.value is null)
                return t => dimensionReflector.GetValue(t) == null;

            if (valueTuple.value is int number)
                return t => (int)dimensionReflector.GetValue(t) == number;

            if (valueTuple.value is string pattern)
                return t => MatchesStringPattern((string)dimensionReflector.GetValue(t), pattern);

            if (valueTuple.value is IEnumerable<string> patterns)
                return t =>
                    patterns.Aggregate(
                        false,
                        (agg, p) =>
                            agg || MatchesStringPattern((string)dimensionReflector.GetValue(t), p)
                    );

            return t => dimensionReflector.GetValue(t) == valueTuple.value;
        }

        private static bool MatchesStringPattern(string value, string pattern)
        {
            if (pattern.StartsWith("!"))
            {
                var actualFilter = pattern.Substring(1, pattern.Length - 1);
                return value != actualFilter;
            }

            // This is string comparison as opposed to object comparison (which is reference based)
            return value == pattern;
        }

        public static IEnumerable<DimensionDescriptor> GetDimensionDescriptors(
            IEnumerable<string> dimensions
        )
        {
            foreach (var dimension in dimensions)
            {
                if (PropertiesByDimension.TryGetValue(dimension, out var desc))
                    yield return desc.Descriptor;
            }
        }
    }
}
