using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using OpenSmc.Data;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.FileStorage;

namespace OpenSmc.Import.Options
{
    public record ImportOptions
    {
        public ImportOptions(string format,
                             IWorkspace targetDataSource, 
                             IFileReadStorage storage,
                             ImmutableList<Func<object, ValidationContext, Task<bool>>> validations,
                             ImmutableDictionary<Type, TableMapping> tableMappings,
                             ImmutableHashSet<string> ignoredTables, 
                             ImmutableHashSet<Type> ignoredTypes, 
                             ImmutableDictionary<string, IEnumerable<string>> ignoredColumns, 
                             bool snapshotModeEnabled)
        {
            Storage = storage;
            TargetDataSource = targetDataSource;
            Format = format;
            SnapshotModeEnabled = snapshotModeEnabled;
            Validations = validations;
            TableMappings = tableMappings ?? ImmutableDictionary<Type, TableMapping>.Empty;
            IgnoredTables = ignoredTables ?? ImmutableHashSet<string>.Empty;
            IgnoredTypes = ignoredTypes ?? ImmutableHashSet<Type>.Empty;
            IgnoredColumns = ignoredColumns ?? ImmutableDictionary<string, IEnumerable<string>>.Empty;
        }

        public string Format { get; init; }
        public IWorkspace TargetDataSource { get; init; }
        public IFileReadStorage Storage { get; init; }
        public bool SnapshotModeEnabled { get; init; }
        public ImmutableList<Func<object, ValidationContext, Task<bool>>> Validations { get; init; }
        public ImmutableDictionary<Type, TableMapping> TableMappings { get; init; }
        public ImmutableHashSet<string> IgnoredTables { get; init; }
        public ImmutableHashSet<Type> IgnoredTypes { get; init; }
        public ImmutableDictionary<string, IEnumerable<string>> IgnoredColumns { get; init; }
    }
}
