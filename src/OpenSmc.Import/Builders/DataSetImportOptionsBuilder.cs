using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using OpenSmc.Activities;
using OpenSmc.DataSetReader;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.DataStructures;
using OpenSmc.DomainDesigner.Abstractions;
using OpenSmc.FileStorage;
using OpenSmc.Import.Mapping;
using OpenSmc.Import.Options;

namespace OpenSmc.Import.Builders
{
    public record DataSetImportOptionsBuilder : ImportOptionsBuilder
    {
        private IDataSet DataSet { get; }

        internal DataSetImportOptionsBuilder(IActivityService activityService,
                                             IDataSetReaderVariable dataSetReaderVariable,
                                             IMappingService mappingService,
                                             IFileReadStorage storage,
                                             CancellationToken cancellationToken,
                                             DomainDescriptor domain,
                                             IDataSource targetSource,
                                             IServiceProvider serviceProvider,
                                             Dictionary<string, Func<ImportOptions, IDataSet, Task>> importFormatFunctions,
                                             ImmutableList<Func<object, ValidationContext, Task<bool>>> defaultValidations,
                                             IDataSet dataSet)
            : base(activityService, dataSetReaderVariable, mappingService, storage, cancellationToken, domain, targetSource, serviceProvider, importFormatFunctions, defaultValidations)
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
