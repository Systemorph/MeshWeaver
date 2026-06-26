using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.DataStructures;

namespace MeshWeaver.Import.Configuration;

/// <summary>
/// An immutable collection of named table mappings that, applied to a data set, populate an
/// <c>EntityStore</c>. Built fluently via <c>MapType</c> / <c>WithTableMapping</c>.
/// </summary>
/// <param name="Workspace">The workspace used to materialize and add mapped instances.</param>
public record ImportMapContainer(IWorkspace Workspace)
{
    internal ImmutableDictionary<string, TableMapping> Mappings { get; init; } =
        ImmutableDictionary<string, TableMapping>.Empty;

    /// <summary>
    /// Registers (or replaces) the mapping for a named table.
    /// </summary>
    /// <param name="name">The data-set table name to map.</param>
    /// <param name="mapping">The mapping that converts that table's rows into entities.</param>
    /// <returns>A new container with the mapping set.</returns>
    public ImportMapContainer WithTableMapping(string name, TableMapping mapping) =>
        this with
        {
            Mappings = Mappings.SetItem(name, mapping)
        };

    /// <summary>
    /// Maps the table whose name matches the type's name onto that type using conventions.
    /// </summary>
    /// <param name="type">The entity type to map rows to.</param>
    /// <returns>A new container with the mapping added.</returns>
    public ImportMapContainer MapType(Type type) => MapType(type.Name, type, x => x);

    /// <summary>
    /// Maps the table named after the type onto that type, with additional mapping configuration.
    /// </summary>
    /// <param name="type">The entity type to map rows to.</param>
    /// <param name="config">Customizes the table-to-type mapping.</param>
    /// <returns>A new container with the mapping added.</returns>
    public ImportMapContainer MapType(
        Type type,
        Func<TableToTypeMapping, TableToTypeMapping> config
    ) => MapType(type.Name, type, config);

    /// <summary>
    /// Maps a named table onto an entity type, with additional mapping configuration.
    /// </summary>
    /// <param name="name">The data-set table name to map.</param>
    /// <param name="type">The entity type to map rows to.</param>
    /// <param name="config">Customizes the table-to-type mapping.</param>
    /// <returns>A new container with the mapping added.</returns>
    public ImportMapContainer MapType(
        string name,
        Type type,
        Func<TableToTypeMapping, TableToTypeMapping> config
    ) => WithTableMapping(name, config.Invoke(new TableToTypeMapping(name, type, Workspace)));

    internal TableMapping? Get(string tableName) =>
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

    /// <summary>
    /// Registers a named table mapping backed by a custom mapping function.
    /// </summary>
    /// <param name="name">The data-set table name to map.</param>
    /// <param name="mappingFunction">Function that transforms the table into the entity store.</param>
    /// <returns>A new container with the mapping added.</returns>
    public ImportMapContainer WithTableMapping(
        string name,
        Func<IDataSet, IDataTable, IWorkspace, EntityStore, Task<EntityStore>> mappingFunction
    )
    {
        var tableMapping = new DirectTableMapping(name, mappingFunction, Workspace);
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
                        new TableToTypeMapping(t.Name, t, Workspace)
                    ))
            )
        };
    }


    /// <summary>
    /// Applies the registered mappings to each table in the data set, accumulating the result.
    /// </summary>
    /// <param name="dataSet">The parsed source data set.</param>
    /// <param name="store">The store to populate.</param>
    /// <returns>The populated entity store.</returns>
    public async Task<EntityStore> ImportAsync(IDataSet dataSet, EntityStore store)
    {
        foreach (var table in dataSet.Tables)
        {
            if (Mappings.TryGetValue(table.TableName!, out var mapping))
                store = await mapping.Map(dataSet, table, store);
        }

        return store;
    }
}
