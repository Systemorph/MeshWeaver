using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using OpenSmc.DataStructures;

namespace OpenSmc.Import
{
    public interface IRowMapping;

    public record RowMapping<T> : IRowMapping
        where T : class
    {
        internal RowMapping(Func<IDataSet, IDataRow, int, T> initializeFunction, ImmutableDictionary<PropertyInfo, Expression> customPropertyMappings)
        {
            InitializeFunction = initializeFunction;
            CustomPropertyMappings = customPropertyMappings;
        }

        public Func<IDataSet, IDataRow, int, T> InitializeFunction { get; init; }
        public ImmutableDictionary<PropertyInfo, Expression> CustomPropertyMappings { get; init; }
    }

    public record ListRowMapping<T> : IRowMapping
        where T : class
    {
        internal ListRowMapping(Func<IDataSet, IDataRow, int, IEnumerable<T>> initializeFunction)
        {
            InitializeFunction = initializeFunction;
        }

        public Func<IDataSet, IDataRow, int, IEnumerable<T>> InitializeFunction { get; init; }
    }
}
