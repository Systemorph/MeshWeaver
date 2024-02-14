using OpenSmc.Data;
using OpenSmc.DataStructures;
using System.Collections.Immutable;

namespace OpenSmc.Import;

public record DomainTypeImporter
{
    public DomainTypeImporter(DataContext dataContext)
    {
        TableMappings = AutoMapper.Create(dataContext);
    }

    public IEnumerable<object> Import(IDataSet dataSet)
    {
        foreach (var table in dataSet.Tables)
        {
            if(TableMappings.TryGetValue(table.TableName, out var mapping))
                foreach (var instance in mapping.Map(dataSet, table))
                    yield return instance;
        }
    }

    internal ImmutableDictionary<string, TableMapping> TableMappings { get; init; }
    internal bool SnapshotModeEnabled { get; init; }



    //public DomainTypeImporter WithType<T>()
    //    where T : class
    //    => WithType<T>(type => type);

    public DomainTypeImporter WithTableMapping(string name, Func<IDataSet, IDataTable, IEnumerable<object>> mappingFunction)
    {
        var tableMapping = new TableMapping(name, mappingFunction);
        return this with { TableMappings = TableMappings.SetItem(tableMapping.TableName, tableMapping) };
    }


    public DomainTypeImporter IgnoreTables(params string[] tableNames) 
        => this with { TableMappings = TableMappings.RemoveRange(tableNames) };
}