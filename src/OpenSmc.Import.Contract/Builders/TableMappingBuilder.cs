using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using OpenSmc.DataStructures;
using OpenSmc.Import.Contract.Mapping;
using OpenSmc.Import.Contract.Options;

namespace OpenSmc.Import.Contract.Builders
{
    public interface ITableMappingBuilder 
    {
        TableMapping GetMapper();
    }

    public record TableMappingBuilder<T> : ITableMappingBuilder, IImportMappingTypeOptionBuilder<T>
        where T : class
    {
        public TableMappingBuilder(string tableName)
        {
            TableName = tableName;
        }
        private IRowMappingBuilder RowMappingBuilderItem { get; init; }

        public TableMappingBuilder<T> SetRowMapping(Func<IDataSet, IDataRow, int, T> initFunction)
        {
            return this with { RowMappingBuilderItem = new RowMappingBuilder().SetInitializeFunction(initFunction) };
        }

        public TableMappingBuilder<T> SetListRowMapping(Func<IDataSet, IDataRow, int, IEnumerable<T>> initFunction)
        {
            return this with { RowMappingBuilderItem = new ListRowMappingBuilder().SetInitializeFunction(initFunction) };
        }
        private string TableName { get; }

        private bool SnapshotModeEnabled { get; init; }

        public IImportMappingTypeOptionBuilder<T> SnapshotMode()
        {
            return this with { SnapshotModeEnabled = true };
        }

        IImportTypeOptionBuilder IImportTypeOptionBuilder.SnapshotMode()
        {
            return SnapshotMode();
        }

        public IImportMappingTypeOptionBuilder<T> MapProperty<TProperty>(Expression<Func<T, TProperty>> selector, Expression<Func<IDataSet, IDataRow, TProperty>> propertyMapping)
        {
            var item = RowMappingBuilderItem is RowMappingBuilder rowMappingBuilder ? rowMappingBuilder : new RowMappingBuilder();
            return this with { RowMappingBuilderItem = item.AddCustomPropertyMappings(selector.GetProperty(), propertyMapping) };
        }

        public TableMapping GetMapper() => new(RowMappingBuilderItem?.GetMapper(), SnapshotModeEnabled, TableName);

        internal interface IRowMappingBuilder
        {
            IRowMapping GetMapper();
        }

        internal record RowMappingBuilder : IRowMappingBuilder
        {
            private Func<IDataSet, IDataRow, int, T> InitializeFunction { get; init; }
            private ImmutableDictionary<PropertyInfo, Expression> CustomPropertyMappings { get; init; } = ImmutableDictionary<PropertyInfo, Expression>.Empty;

            public RowMappingBuilder SetInitializeFunction(Func<IDataSet, IDataRow, int, T> initializeFunction)
            {
                return this with { InitializeFunction = initializeFunction };
            }

            public RowMappingBuilder AddCustomPropertyMappings(PropertyInfo propertyInfo, Expression expression)
            {   
                return this with { CustomPropertyMappings = CustomPropertyMappings.SetItem(propertyInfo, expression) };
            }

            public IRowMapping GetMapper() => new RowMapping<T>(InitializeFunction, CustomPropertyMappings);
        }

        internal record ListRowMappingBuilder : IRowMappingBuilder
        {
            private Func<IDataSet, IDataRow, int, IEnumerable<T>> InitializeFunction { get; init; }

            public ListRowMappingBuilder SetInitializeFunction(Func<IDataSet, IDataRow, int, IEnumerable<T>> initializeFunction)
            {
                return this with { InitializeFunction = initializeFunction };
            }

            public IRowMapping GetMapper() => new ListRowMapping<T>(InitializeFunction);
        }
    }
}
