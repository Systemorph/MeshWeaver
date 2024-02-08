﻿using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.DomainDesigner.Abstractions;
using OpenSmc.FileStorage;

namespace OpenSmc.Import.Contract.Options
{
    public record StreamImportOptions : ImportOptions
    {
        public Stream Stream { get; init; }

        public StreamImportOptions(Stream stream, 
                                   string format, 
                                   DomainDescriptor domain, 
                                   IDataSource targetDataSource, 
                                   IFileReadStorage storage,
                                   ImmutableList<Func<object, ValidationContext, Task<bool>>> validations,
                                   ImmutableDictionary<Type, TableMapping> tableMappings, 
                                   ImmutableHashSet<string> ignoredTables, ImmutableHashSet<Type> ignoredTypes, 
                                   ImmutableDictionary<string, IEnumerable<string>> ignoredColumns, 
                                   bool snapshotModeEnabled)
            : base(format, domain, targetDataSource, storage, validations, tableMappings, ignoredTables, ignoredTypes, ignoredColumns, snapshotModeEnabled)
        {
            Stream = stream;
        }
    }
}
