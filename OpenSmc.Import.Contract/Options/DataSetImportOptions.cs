﻿using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.DataStructures;
using OpenSmc.DomainDesigner.Abstractions;
using OpenSmc.FileStorage;

namespace OpenSmc.Import.Contract.Options
{
    public record DataSetImportOptions : ImportOptions
    {
        public IDataSet DataSet { get; init; }

        public DataSetImportOptions(IDataSet dataSet, 
                                    string format, 
                                    DomainDescriptor domain, 
                                    IDataSource targetDataSource, 
                                    IFileReadStorage storage,
                                    ImmutableList<Func<object, ValidationContext, Task<bool>>> validations,
                                    ImmutableDictionary<Type, TableMapping> tableMappings,
                                    ImmutableHashSet<string> ignoredTables,
                                    ImmutableHashSet<Type> ignoredTypes,
                                    ImmutableDictionary<string, IEnumerable<string>> ignoredColumns,
                                    bool snapshotModeEnabled)
            : base(format, domain, targetDataSource, storage, validations, tableMappings, ignoredTables, ignoredTypes, ignoredColumns, snapshotModeEnabled)
        {
            DataSet = dataSet;
        }
    }
}
