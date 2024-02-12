using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using OpenSmc.DataStructures;

namespace OpenSmc.Import
{
    public record TableMapping(string TableName, Func<IDataSet, IDataTable, IReadOnlyCollection<object>> MappingFunction)
    {
        internal bool SnapshotModeEnabled { get; init; }

        public TableMapping SnapshotMode(bool enable = true)
        {
            return this with { SnapshotModeEnabled = enable };
        }

        public virtual IReadOnlyCollection<object> Map(IDataSet dataSet, IDataTable dataSetTable)
            => MappingFunction.Invoke(dataSet, dataSetTable);
    }

    public record TableToTypeMapping<T>(string TableName) : TableMapping(TableName, null)
        where T : class
    {
        public override IReadOnlyCollection<object> Map(IDataSet dataSet, IDataTable dataSetTable)
        {
            return null;
        }

        internal Func<IDataSet, IDataRow, int, T> RowMapping { get; init; } 

        public TableToTypeMapping<T> WithRowMapping(Func<IDataSet, IDataRow, int, T> initFunction)
        {
            return this with { RowMapping = initFunction };
        }

        internal Func<IDataSet, IDataRow, int, IEnumerable<T>> ListPropertyMapping { get; init; }
        public TableToTypeMapping<T> SetListRowMapping(Func<IDataSet, IDataRow, int, IEnumerable<T>> listPropertyMapping)
            => this with { ListPropertyMapping = listPropertyMapping };



        ImmutableDictionary<PropertyInfo, Expression<Func<IDataSet, IDataRow, object>>> PropertyMappings = ImmutableDictionary<PropertyInfo, Expression<Func<IDataSet, IDataRow, object>>>.Empty;

        public TableToTypeMapping<T> MapProperty<TProperty>(Expression<Func<T, TProperty>> selector,
            Expression<Func<IDataSet, IDataRow, object>> propertyMapping)
            => this with { PropertyMappings = PropertyMappings.SetItem(selector.GetProperty(), propertyMapping) };



    }
}
