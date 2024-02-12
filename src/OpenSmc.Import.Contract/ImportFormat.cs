using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using OpenSmc.Collections;
using OpenSmc.DataSetReader.Abstractions;
using OpenSmc.DataStructures;

namespace OpenSmc.Import;

public record ImportFormat(string Format)
{
    public const string Default = nameof(Default);

    internal ImmutableDictionary<string, TableMapping> TableMappings { get; init; } = ImmutableDictionary<string, TableMapping>.Empty;
    internal ImmutableHashSet<string> IgnoredTables { get; init; } = ImmutableHashSet<string>.Empty;
    internal ImmutableHashSet<Type> IgnoredTypes { get; init; } = ImmutableHashSet<Type>.Empty;
    internal ImmutableDictionary<string, IEnumerable<string>> IgnoredColumns { get; init; } = ImmutableDictionary<string, IEnumerable<string>>.Empty;
    internal bool SnapshotModeEnabled { get; init; }
    internal ImmutableList<Func<object, ValidationContext, bool>> Validations;
    public ImportFormat WithValidation(Func<object, ValidationContext, bool> validationRule)
    {
        return this with { Validations = Validations.Add(validationRule) };
    }



    public ImportFormat WithType<T>()
        where T : class
        => WithType<T>(type => type);

    public ImportFormat WithType<T>(Func<TableToTypeMapping<T>, TableToTypeMapping<T>> mapping)
        where T : class
    {
        var tableMapping = mapping.Invoke(new(typeof(T).Name));
        return this with { TableMappings = TableMappings.SetItem(tableMapping.TableName, tableMapping) };
    }


    public ImportFormat IgnoreType<T>()
    {
        return this with { IgnoredTypes = IgnoredTypes.Add(typeof(T)) };
    }

    public ImportFormat IgnoreTypes(params Type[] types)
    {
        return this with { IgnoredTypes = IgnoredTypes.Union(types) };
    }

    public ImportFormat IgnoreTables(params string[] tableNames)
    {
        return this with { IgnoredTables = IgnoredTables.Union(tableNames) };
    }

    public ImportFormat IgnoreColumn(string columnName, string tableName)
    {
        return string.IsNullOrEmpty(columnName) ? this : IgnoreColumnInner(columnName.RepeatOnce(), tableName);
    }

    public ImportFormat IgnoreColumns(IEnumerable<string> columnNames, string tableName)
    {
        return IgnoreColumnInner(columnNames, tableName);
    }

    private ImportFormat IgnoreColumnInner(IEnumerable<string> columnNames, string tableName)
    {
        columnNames = columnNames?.ToList() ?? new List<string>();

        if (!columnNames.Any())
            return this;

        if (IgnoredColumns.TryGetValue(tableName, out var ret))
            columnNames = columnNames.Union(ret);

        return this with { IgnoredColumns = IgnoredColumns.SetItem(tableName, columnNames) };
    }





    private bool SaveLog { get; init; }
    public ImportFormat SaveLogs(bool save = true)
    {
        return this with { SaveLog = save };
    }


    private ImmutableList<ImportFunction> ImportFunctions { get; init; } = ImmutableList<ImportFunction>.Empty;

    public ImportFormat WithImportFunction(ImportFunction importFunction) =>
        this with { ImportFunctions = ImportFunctions.Add(importFunction) };

    public IReadOnlyCollection<object> Import(ImportRequest importRequest, object dataSet)
        => ImportFunctions.Select(f => f.Invoke(importRequest, dataSet)).Aggregate((x,y) => x.Concat(y)).ToArray();

    public delegate IEnumerable<object> ImportFunction(ImportRequest importRequest, object dataSet);
}