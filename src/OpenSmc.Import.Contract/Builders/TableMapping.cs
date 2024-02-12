using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using OpenSmc.Data;
using OpenSmc.DataStructures;
using OpenSmc.Import.Options;

namespace OpenSmc.Import.Builders
{
    public record TableMapping(string TableName)
    {
        public IReadOnlyCollection<object> Map(IDataSet dataSet, IDataTable dataSetTable)
        {
            throw new NotImplementedException();
        }
    }

    public record TableMapping<T>(string TableName) : TableMapping(TableName)
        where T : class
    {
        internal Func<IDataSet, IDataRow, int, T> RowMapping { get; init; }

        public TableMapping<T> WithRowMapping(Func<IDataSet, IDataRow, int, T> initFunction)
        {
            return this with { RowMapping = initFunction };
        }

        internal Func<IDataSet, IDataRow, int, IEnumerable<T>> ListPropertyMapping { get; init; }
        public TableMapping<T> SetListRowMapping(Func<IDataSet, IDataRow, int, IEnumerable<T>> listPropertyMapping)
            => this with { ListPropertyMapping = listPropertyMapping };

        internal bool SnapshotModeEnabled { get; init; }

        public TableMapping<T> SnapshotMode(bool enable = true)
        {
            return this with { SnapshotModeEnabled = enable };
        }


        ImmutableDictionary<PropertyInfo, Expression<Func<IDataSet, IDataRow, object>>> PropertyMappings = ImmutableDictionary<PropertyInfo, Expression<Func<IDataSet, IDataRow, object>>>.Empty;

        public TableMapping<T> MapProperty<TProperty>(Expression<Func<T, TProperty>> selector,
            Expression<Func<IDataSet, IDataRow, object>> propertyMapping)
            => this with { PropertyMappings = PropertyMappings.SetItem(selector.GetProperty(), propertyMapping) };



    }
}
