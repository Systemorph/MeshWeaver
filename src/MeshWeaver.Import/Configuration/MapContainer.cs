using System.Collections.Immutable;
using MeshWeaver.DataStructures;

namespace MeshWeaver.Import.Configuration;

public record ImportMapContainer
{
    internal ImmutableDictionary<string, TableMapping> Mappings { get; init; } =
        ImmutableDictionary<string, TableMapping>.Empty;

    public ImportMapContainer WithTableMapping(string name, TableMapping mapping) =>
        this with
        {
            Mappings = Mappings.SetItem(name, mapping)
        };

    public ImportMapContainer MapType(Type type) => MapType(type.Name, type, x => x);

    public ImportMapContainer MapType(
        Type type,
        Func<TableToTypeMapping, TableToTypeMapping> config
    ) => MapType(type.Name, type, config);

    public ImportMapContainer MapType(
        string name,
        Type type,
        Func<TableToTypeMapping, TableToTypeMapping> config
    ) => WithTableMapping(name, config.Invoke(new TableToTypeMapping(name, type)));

    internal TableMapping Get(string tableName) =>
        Mappings.TryGetValue(tableName, out var mapping) ? mapping : null;

    internal ImportMapContainer SetItem(string name, TableMapping tableMapping) =>
        this with
        {
            Mappings = Mappings.SetItem(name, tableMapping)
        };

    internal ImportMapContainer RemoveRange(params string[] tableNames) =>
        this with
        {
            Mappings = Mappings.RemoveRange(tableNames)
        };

    public ImportMapContainer WithTableMapping(
        string name,
        Func<IDataSet, IDataTable, IEnumerable<object>> mappingFunction
    )
    {
        var tableMapping = new DirectTableMapping(name, mappingFunction);
        return this with { Mappings = Mappings.SetItem(tableMapping.Name, tableMapping) };
    }

    internal ImportMapContainer WithAutoMappingsForTypes(IReadOnlyCollection<Type> mappedTypes)
    {
        return this with
        {
            Mappings = Mappings.SetItems(
                mappedTypes
                    .Where(t => !Mappings.ContainsKey(t.Name))
                    .Select(t => new KeyValuePair<string, TableMapping>(
                        t.Name,
                        new TableToTypeMapping(t.Name, t)
                    ))
            )
        };
    }

    public IEnumerable<object> Import(IDataSet dataSet)
    {
        foreach (var table in dataSet.Tables)
        {
            if (Mappings.TryGetValue(table.TableName, out var mapping))
                foreach (var instance in mapping.Map(dataSet, table))
                    yield return instance;
        }
    }
}
