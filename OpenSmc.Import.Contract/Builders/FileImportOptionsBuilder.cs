using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using OpenSmc.Activities;
using OpenSmc.DataSetReader;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.DataStructures;
using OpenSmc.DomainDesigner.Abstractions;
using OpenSmc.FileStorage;
using OpenSmc.Import.Contract.Mapping;
using OpenSmc.Import.Contract.Options;

namespace OpenSmc.Import.Contract.Builders
{
    public record FileReaderImportOptionsBuilder : ImportOptionsBuilder
    {
        private string FilePath { get; }

        public FileReaderImportOptionsBuilder(IActivityService activityService,
                                                IMappingService mappingService,
                                                IFileReadStorage storage,
                                                CancellationToken cancellationToken, 
                                                DomainDescriptor domain, 
                                                IDataSource targetSource,
                                                IServiceProvider serviceProvider,
                                                Dictionary<string, Func<ImportOptions, IDataSet, Task>> importFormatFunctions,
                                                ImmutableList<Func<object, ValidationContext, Task<bool>>> defaultValidations, 
                                                string filePath)
            : base(activityService, mappingService, storage, cancellationToken, domain, targetSource, serviceProvider, importFormatFunctions, defaultValidations)
        {
            FilePath = filePath;
        }

        protected override async Task<(IDataSet DataSet, string Format)> GetDataSetAsync()
        {
            if (Storage == null)
                throw new ArgumentException("Please specify file storage to get file from.");

            var stream = await Storage.ReadAsync(FilePath, CancellationToken);
            var optionsBuilder = DataSetReaderOptionsBuilderFunc(DataSetReaderVariable.ReadFromStream(stream));
            var contentType = ((DataSetReaderOptionsBuilder)optionsBuilder).ContentType ?? Path.GetExtension(FilePath);
            return await optionsBuilder.WithContentType(contentType).ExecuteAsync();
        }

        public override FileImportOptions GetImportOptions()
        {
            return new FileImportOptions(FilePath, 
                                         Format, 
                                         Domain, 
                                         TargetDataSource, 
                                         Storage, 
                                         Validations,
                                         TableMappingBuilders.ToImmutableDictionary(x => x.Key,
                                                                                    x => x.Value.GetMapper()),
                                         IgnoredTables, 
                                         IgnoredTypes, 
                                         IgnoredColumns, 
                                         SnapshotModeEnabled);
        }
    }
}
