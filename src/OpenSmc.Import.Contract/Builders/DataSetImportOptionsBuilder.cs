using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.DataStructures;
using OpenSmc.FileStorage;
using OpenSmc.Import.Mapping;
using OpenSmc.Import.Options;

namespace OpenSmc.Import.Builders
{
    public record DataSetImportOptionsBuilder : ImportOptionsBuilder
    {
        private IDataSet DataSet { get; }

        public DataSetImportOptionsBuilder(IActivityService activityService,
                                             IMappingService mappingService,
                                             IFileReadStorage storage,
                                             CancellationToken cancellationToken,
                                             IWorkspace targetSource,
                                             IServiceProvider serviceProvider,
                                             Dictionary<string, Func<ImportOptions, IDataSet, Task>> importFormatFunctions,
                                             ImmutableList<Func<object, ValidationContext, Task<bool>>> defaultValidations,
                                             IDataSet dataSet)
            : base(activityService, mappingService, storage, cancellationToken, targetSource, serviceProvider, importFormatFunctions, defaultValidations)
        {
            DataSet = dataSet;
        }

        protected override Task<(IDataSet DataSet, string Format)> GetDataSetAsync()
        {
            //here we don't want to send format
            return Task.FromResult((DataSet, (string)null));
        }

        public override DataSetImportOptions GetImportOptions() => new(DataSet, Format, TargetDataSource, Storage, Validations,
                                                                       TableMappingBuilders.ToImmutableDictionary(x => x.Key,
                                                                                                                  x => x.Value.GetMapper()),
                                                                       IgnoredTables, IgnoredTypes, IgnoredColumns, SnapshotModeEnabled);

    }
}
