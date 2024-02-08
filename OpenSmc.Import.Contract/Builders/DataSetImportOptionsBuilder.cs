﻿using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using OpenSmc.Activities;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.DataStructures;
using OpenSmc.DomainDesigner.Abstractions;
using OpenSmc.FileStorage;
using OpenSmc.Import.Contract.Mapping;
using OpenSmc.Import.Contract.Options;

namespace OpenSmc.Import.Contract.Builders
{
    public record DataSetImportOptionsBuilder : ImportOptionsBuilder
    {
        private IDataSet DataSet { get; }

        public DataSetImportOptionsBuilder(IActivityService activityService,
                                             IMappingService mappingService,
                                             IFileReadStorage storage,
                                             CancellationToken cancellationToken,
                                             DomainDescriptor domain,
                                             IDataSource targetSource,
                                             IServiceProvider serviceProvider,
                                             Dictionary<string, Func<ImportOptions, IDataSet, Task>> importFormatFunctions,
                                             ImmutableList<Func<object, ValidationContext, Task<bool>>> defaultValidations,
                                             IDataSet dataSet)
            : base(activityService, mappingService, storage, cancellationToken, domain, targetSource, serviceProvider, importFormatFunctions, defaultValidations)
        {
            DataSet = dataSet;
        }

        protected override Task<(IDataSet DataSet, string Format)> GetDataSetAsync()
        {
            //here we don't want to send format
            return Task.FromResult((DataSet, (string)null));
        }

        public override DataSetImportOptions GetImportOptions() => new(DataSet, Format, Domain, TargetDataSource, Storage, Validations,
                                                                       TableMappingBuilders.ToImmutableDictionary(x => x.Key,
                                                                                                                  x => x.Value.GetMapper()),
                                                                       IgnoredTables, IgnoredTypes, IgnoredColumns, SnapshotModeEnabled);

    }
}
