﻿using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.DataSetReader;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.DataStructures;
using OpenSmc.FileStorage;
using OpenSmc.Import.Mapping;
using OpenSmc.Import.Options;

namespace OpenSmc.Import.Builders
{
    public record StringImportOptionsBuilder : ImportOptionsBuilder
    {
        private string Content { get; }

        public StringImportOptionsBuilder(IActivityService activityService,
                                            IMappingService mappingService,
                                            IFileReadStorage storage,
                                            CancellationToken cancellationToken,
                                            IWorkspace targetSource,
                                            IServiceProvider serviceProvider,
                                            Dictionary<string, Func<ImportOptions, IDataSet, Task>> importFormatFunctions,
                                            ImmutableList<Func<object, ValidationContext, Task<bool>>> defaultValidations,
                                            string content)
            : base(activityService, mappingService, storage, cancellationToken, targetSource, serviceProvider, importFormatFunctions, defaultValidations)
        {
            Content = content;
        }

        protected override async Task<(IDataSet DataSet, string Format)> GetDataSetAsync()
        {
            var stream = await Task.FromResult((Stream)new MemoryStream(Encoding.ASCII.GetBytes(Content)));
            var optionsBuilder = DataSetReaderOptionsBuilderFunc(DataSetReaderVariable.ReadFromStream(stream));
            var contentType = ((DataSetReaderOptionsBuilder)optionsBuilder).ContentType;
            return await optionsBuilder.WithContentType(contentType).ExecuteAsync();
        }

        public override StringImportOptions GetImportOptions()
        {
            return new(Content, Format, TargetDataSource, Storage, Validations,
                       TableMappingBuilders.ToImmutableDictionary(x => x.Key,
                                                                  x => x.Value.GetMapper()),
                       IgnoredTables, IgnoredTypes, IgnoredColumns, SnapshotModeEnabled);
        }
    }
}