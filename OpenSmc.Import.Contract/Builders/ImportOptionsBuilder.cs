using System.Collections;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenSmc.Activities;
using OpenSmc.Collections;
using OpenSmc.DataSetReader;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.DataStructures;
using OpenSmc.DomainDesigner.Abstractions;
using OpenSmc.FileStorage;
using OpenSmc.Import.Contract.Mapping;
using OpenSmc.Import.Contract.Options;
using OpenSmc.Messaging;
using OpenSmc.Reflection;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Import.Contract.Builders
{
    public static class ImportRegistryExtensions
    {
        public static MessageHubConfiguration AddImport(this MessageHubConfiguration configuration,
            Func<ImportOptionsBuilder, ImportOptionsBuilder> importConfiguration)
        {
            //var options = importConfiguration.Invoke(new);
            //return configuration.WithServices(services => services.AddSingleton<IActivityService>())
            //    .AddPlugin(hub => new ImportPlugin(hub, options));
            throw new NotImplementedException();
            //TODO: implement
        }
    }
    /*
     * Create a .Contract folder
     * create ImportRequest (all options for import), must be serializable
     * types should go to type registry (manually or from dataplugin)
     *
     * format specification goes to add plugin
     *
     * hubs (tests use only hubs)
     *
     */

    public class ImportPlugin : MessageHubPlugin<ImportPlugin>
    {
        [Inject] private IActivityService activityService;

        public ImportPlugin(IMessageHub hub, ImportOptionsBuilder options) : base(hub)
        {
            
        }
    }

    public abstract record ImportOptionsBuilder
    {
        // TODO V10: Is this a builder? Separate API from implementation (30.01.2024, Valery Petrov)
        private protected IDataSetReaderVariable DataSetReaderVariable;
        //this will be used in the future to handle properly progress
        // ReSharper disable once NotAccessedField.Local
        private protected readonly CancellationToken CancellationToken;
        private protected ImmutableList<Func<object, ValidationContext, Task<bool>>> Validations;
        private protected IActivityService ActivityService;
        private protected IMappingService MappingService;

        private protected string Format { get; private set; }
        private protected DomainDescriptor Domain { get; init; } = new();
        private protected IDataSource TargetDataSource { get; init; }
        private protected IServiceProvider ServiceProvider { get; init; }
        private protected IFileReadStorage Storage { get; init; }
        private protected Dictionary<string, Func<ImportOptions, IDataSet, Task>> ImportFormatFunctions { get; init; }

        private protected ImmutableDictionary<Type, ITableMappingBuilder> TableMappingBuilders { get; init; } = ImmutableDictionary<Type, ITableMappingBuilder>.Empty;
        private protected ImmutableHashSet<string> IgnoredTables { get; init; } = ImmutableHashSet<string>.Empty;
        private protected ImmutableHashSet<Type> IgnoredTypes { get; init; } = ImmutableHashSet<Type>.Empty;
        //tableName => { ignoredColumns }
        private protected ImmutableDictionary<string, IEnumerable<string>> IgnoredColumns { get; init; } = ImmutableDictionary<string, IEnumerable<string>>.Empty;

        private protected bool SnapshotModeEnabled { get; init; }

        private protected Func<IDataSetReaderOptionsBuilder, IDataSetReaderOptionsBuilder> DataSetReaderOptionsBuilderFunc { get; init; } = ds => ds;

        protected internal ImportOptionsBuilder(IActivityService activityService,
                                                IMappingService mappingService,
                                                IFileReadStorage storage,
                                                CancellationToken cancellationToken,
                                                DomainDescriptor domain,
                                                IDataSource targetSource,
                                                IServiceProvider serviceProvider,
                                                Dictionary<string, Func<ImportOptions, IDataSet, Task>> importFormatFunctions,
                                                ImmutableList<Func<object, ValidationContext, Task<bool>>> defaultValidations)
        {
            ActivityService = activityService;
            MappingService = mappingService;
            Storage = storage;
            Domain = domain;
            TargetDataSource = targetSource;
            CancellationToken = cancellationToken;
            ImportFormatFunctions = importFormatFunctions;
            Validations = defaultValidations;
            ServiceProvider = serviceProvider;
        }

        public ImportOptionsBuilder WithDomain(DomainDescriptor domainDescriptor)
        {
            return this with { Domain = domainDescriptor };
        }

        public ImportOptionsBuilder WithFileStorage(IFileReadStorage storage)
        {
            return this with { Storage = storage };
        }

        public ImportOptionsBuilder WithValidation(Func<object, ValidationContext, Task<bool>> validationRule)
        {
            validationRule ??= (_, _) => Task.FromResult(true);
            return this with { Validations = Validations.Add(validationRule) };
        }

        public ImportOptionsBuilder WithValidation(Func<object, ValidationContext, bool> validationRule)
        {
            return WithValidation((obj, vc) =>
                                  {
                                      validationRule ??= (_, _) => true;
                                      var ret = validationRule(obj, vc);
                                      return Task.FromResult(ret);
                                  });
        }

        public ImportOptionsBuilder WithType<TType>(Func<IImportMappingTypeOptionBuilder<TType>, IImportMappingTypeOptionBuilder<TType>> importOptions = default)
            where TType : class
        {
            return WithType(typeof(TType).Name, importOptions);
        }

        public ImportOptionsBuilder WithType<TType>(string tableName, Func<IImportMappingTypeOptionBuilder<TType>, IImportMappingTypeOptionBuilder<TType>> importOptions = default)
            where TType : class
        {
            tableName ??= typeof(TType).Name;
            importOptions ??= x => x;
            return WithTypeInner((TableMappingBuilder<TType>)importOptions(new TableMappingBuilder<TType>(tableName)));
        }

        public ImportOptionsBuilder WithType<TType>(Func<IDataSet, IDataRow, TType> initFunction, string tableName = default, Func<IImportTypeOptionBuilder, IImportTypeOptionBuilder> importOptions = default)
            where TType : class
        {
            return WithType((ds, row, _) => initFunction(ds, row), tableName, importOptions);
        }

        public ImportOptionsBuilder WithType<TType>(Func<IDataSet, IDataRow, int, TType> initFunction, string tableName = default, Func<IImportTypeOptionBuilder, IImportTypeOptionBuilder> importOptions = default)
            where TType : class
        {
            tableName ??= typeof(TType).Name;
            importOptions ??= x => x;
            return WithTypeInner(((TableMappingBuilder<TType>)importOptions(new TableMappingBuilder<TType>(tableName))).SetRowMapping(initFunction));
        }

        public ImportOptionsBuilder WithType<TType>(Func<IDataSet, IDataRow, IEnumerable<TType>> initFunction, string tableName = default, Func<IImportTypeOptionBuilder, IImportTypeOptionBuilder> importOptions = default)
            where TType : class
        {
            return WithType((ds, row, _) => initFunction(ds, row), tableName, importOptions);
        }

        public ImportOptionsBuilder WithType<TType>(Func<IDataSet, IDataRow, int, IEnumerable<TType>> initFunction, string tableName = default, Func<IImportTypeOptionBuilder, IImportTypeOptionBuilder> importOptions = default)
            where TType : class
        {
            tableName ??= typeof(TType).Name;
            importOptions ??= x => x;
            return WithTypeInner(((TableMappingBuilder<TType>)importOptions(new TableMappingBuilder<TType>(tableName))).SetListRowMapping(initFunction));
        }

        private ImportOptionsBuilder WithTypeInner<TType>(TableMappingBuilder<TType> tableMapper​Builder)
            where TType : class
        {
            var enumerableType = typeof(TType).GetEnumerableElementType();
            if (enumerableType != null)
                throw new ArgumentException("Please, specify correct generic type for using such function.");

            return this with { TableMappingBuilders = TableMappingBuilders.SetItem(typeof(TType), tableMapper​Builder) };
        }

        public ImportOptionsBuilder IgnoreType<T>()
        {
            return this with { IgnoredTypes = IgnoredTypes.Add(typeof(T)) };
        }

        public ImportOptionsBuilder IgnoreTypes(params Type[] types)
        {
            return this with { IgnoredTypes = IgnoredTypes.Union(types) };
        }

        public ImportOptionsBuilder IgnoreTables(params string[] tableNames)
        {
            return this with { IgnoredTables = IgnoredTables.Union(tableNames) };
        }

        public ImportOptionsBuilder IgnoreColumn(string columnName, string tableName)
        {
            return string.IsNullOrEmpty(columnName) ? this : IgnoreColumnInner(columnName.RepeatOnce(), tableName);
        }

        public ImportOptionsBuilder IgnoreColumns(IEnumerable<string> columnNames, string tableName)
        {
            return IgnoreColumnInner(columnNames, tableName);
        }

        private ImportOptionsBuilder IgnoreColumnInner(IEnumerable<string> columnNames, string tableName)
        {
            columnNames = columnNames?.ToList() ?? new List<string>();

            if (!columnNames.Any())
                return this;

            if (IgnoredColumns.TryGetValue(tableName, out var ret))
                columnNames = columnNames.Union(ret);

            return this with { IgnoredColumns = IgnoredColumns.SetItem(tableName, columnNames) };
        }

        public ImportOptionsBuilder WithFormat(string format)
        {
            return this with { Format = format };
        }

        public ImportOptionsBuilder SnapshotMode()
        {
            return this with { SnapshotModeEnabled = true };
        }

        public ImportOptionsBuilder WithTarget(IDataSource dataSource)
        {
            return this with { TargetDataSource = dataSource };
        }

        public ImportOptionsBuilder WithReadingOptions(Func<IDataSetReaderOptionsBuilder, IDataSetReaderOptionsBuilder> readFunc)
        {
            if (readFunc == null)
                throw new ArgumentNullException(nameof(readFunc));
            return this with { DataSetReaderOptionsBuilderFunc = readFunc };
        }

        protected abstract Task<(IDataSet DataSet, string Format)> GetDataSetAsync();


        private bool SaveLog { get; init; }
        public ImportOptionsBuilder SaveLogs(bool save = true)
        {
            return this with { SaveLog = save };
        }

        public async Task<ActivityLog> ExecuteAsync()
        {
            ActivityLog log;
            ActivityService.Start(); // TODO: Add category (2022.01.18, Armen Sirotenko)

            try
            {
                var importMappings = this;

                var (dataSet, format) = await GetDataSetAsync();
                //even if format was defined we take it from file
                if (format != null)
                    importMappings = importMappings with { Format = format };

                var importOptions = importMappings.GetImportOptions();

                // in case if import format function is defined, then we get this function and execute
                if (importMappings.Format != null && ImportFormatFunctions.TryGetValue(importOptions.Format, out var importFunction))
                    await importFunction(importOptions, dataSet);
                else
                {
                    if (TargetDataSource == null)
                        throw new ArgumentException("Please specify target.");

                    var instancesPerTypes = await MappingService.MapAsync(dataSet,
                                                                          importOptions,
                                                                          ActivityService,
                                                                          ServiceProvider,
                                                                          new Dictionary<object, object>(),
                                                                          CancellationToken);


                    if (!ActivityService.HasErrors())
                    {
                        foreach (var instancesPerType in instancesPerTypes)
                        {
                            var isSnapshot = importOptions.SnapshotModeEnabled ||
                                             importOptions.TableMappings.TryGetValue(instancesPerType.Key, out var tableMapping) &&
                                             tableMapping.SnapshotModeEnabled;
                            if (instancesPerType.Value.Count > 0 || isSnapshot)
                                await UpdateMethod.MakeGenericMethod(instancesPerType.Key).InvokeAsActionAsync(TargetDataSource, instancesPerType.Value, isSnapshot);
                        }
                       
                        await TargetDataSource.CommitAsync();
                    }
                }
            }
            catch (Exception e)
            {
                var message = new StringBuilder(e.Message);
                while (e.InnerException != null)
                {
                    message.AppendLine(e.InnerException.Message);
                    e = e.InnerException;
                }

                ActivityService.LogError(message.ToString());
            }
            finally
            {
                log = ActivityService.Finish();
                if (SaveLog && TargetDataSource != null)
                {
                    await TargetDataSource.UpdateAsync(new[]{log});
                    await TargetDataSource.CommitAsync();
                }
            }

            return log;
        }
#pragma warning disable 4014
        private static readonly IGenericMethodCache UpdateMethod =
            GenericCaches.GetMethodCacheStatic(() => PerformUpdate<object>(null, null, false));
#pragma warning disable 4014

        private static async Task PerformUpdate<T>(IDataSource targetDataSource, ICollection items, bool isSnapshot)
        {
            await targetDataSource.UpdateAsync(items as IEnumerable<T>, o => isSnapshot ? o.SnapshotMode() : o);
        }

        public abstract ImportOptions GetImportOptions();
    }
}
