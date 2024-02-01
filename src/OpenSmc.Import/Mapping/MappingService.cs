using System.Collections;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using OpenSmc.Collections;
using OpenSmc.DataStructures;
using OpenSmc.Import.Options;
using OpenSmc.Reflection;

namespace OpenSmc.Import.Mapping
{
    public interface IMappingService
    {
        Task<Dictionary<Type, ICollection>> MapAsync([NotNull] IDataSet dataSet, 
                                                     ImportOptions importOptions, 
                                                     ILogger logger, 
                                                     IServiceProvider serviceProvider,
                                                     Dictionary<object, object> validationCache,
                                                     CancellationToken cancellationToken = default);
    }

    public class MappingService : IMappingService
    {
        public static string ValidationStageFailed = "Validation stage of type {0} has failed.";

        public async Task<Dictionary<Type, ICollection>> MapAsync(IDataSet dataSet,
                                                                  ImportOptions importOptions,
                                                                  ILogger logger,
                                                                  IServiceProvider serviceProvider,
                                                                  Dictionary<object, object> validationCache,
                                                                  CancellationToken cancellationToken = default)
        {
            var ret = new Dictionary<Type, ICollection>();

            // null cases still can happens
            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            // ReSharper disable once HeuristicUnreachableCode
            if (dataSet == null)
                return ret;

            var filteredTableMappings = importOptions.TableMappings.Where(x => !importOptions.IgnoredTables.Contains(x.Value.TableName) &&
                                                                               !importOptions.IgnoredTypes.Contains(x.Key)).ToList();

            //handle mappings
            foreach ((Type type, TableMapping tableMapping) in filteredTableMappings)
            {
                if (dataSet.Tables.Contains(tableMapping.TableName))
                {
                    var items = await PerformActions(new MappingServiceArgs(type,
                                                                            tableMapping.RowMapping,
                                                                            importOptions.Validations,
                                                                            serviceProvider,
                                                                            validationCache,
                                                                            dataSet,
                                                                            dataSet.Tables[tableMapping.TableName],
                                                                            logger,
                                                                            importOptions.IgnoredColumns.GetValueOrDefault(tableMapping.TableName),
                                                                            cancellationToken));
                    ret[type] = items;
                }
                else //mapper have no corresponding table in dataSet
                {
                    logger.LogWarning($"Table {tableMapping.TableName} was not found.");
                }
            }


            var domainTypesToExecute = importOptions.Domain?.Types?
                                                    .Where(x => !importOptions.IgnoredTypes.Contains(x) && //filter out ignored types
                                                                !importOptions.IgnoredTables.Contains(x.Name) &&
                                                                !ret.ContainsKey(x) && //filter out what was imported by TableMappings, to not override it
                                                                dataSet.Tables.Contains(x.Name))
                                                    .ToList()
                                       ?? new List<Type>();

            //handle case with domain types
            foreach (var domainType in domainTypesToExecute)
                ret.Add(domainType, await PerformActions(new MappingServiceArgs(domainType,
                                                                                null,
                                                                                importOptions.Validations,
                                                                                serviceProvider,
                                                                                validationCache,
                                                                                dataSet,
                                                                                dataSet.Tables[domainType.Name],
                                                                                logger,
                                                                                importOptions.IgnoredColumns.GetValueOrDefault(domainType.Name),
                                                                                cancellationToken)));

            return ret;
        }

        private async Task<ICollection> PerformActions(MappingServiceArgs args)
        {
            return (ICollection)await PerformTypeActionsMethod.MakeGenericMethod(args.Type).InvokeAsFunctionAsync(this, args);
        }

#pragma warning disable 4014
        private static readonly IGenericMethodCache PerformTypeActionsMethod =
            GenericCaches.GetMethodCache<MappingService>(x => x.PerformTypeActions<object>(null));
#pragma warning restore 4014

        protected async Task<ICollection> PerformTypeActions<T>(MappingServiceArgs args)
            where T : class
        {
            if (args.RowMapping is ListRowMapping<T> listRowMapping)
                return await CreateAndValidate(args, (ds, row, index) => listRowMapping.InitializeFunction(ds, row, index));

            var rowMapping = args.RowMapping as RowMapping<T>;
            var ignored = args.IgnoredColumns ?? Enumerable.Empty<string>();
            var columns = args.Table.Columns.Where(x => !ignored.Contains(x.ColumnName)).ToDictionary(x => x.ColumnName, x => x.Index);
            rowMapping = await AutoMapper.AddAutoMapping(rowMapping, columns);

            return await CreateAndValidate(args, (ds, row, index) => rowMapping.InitializeFunction(ds, row, index).RepeatOnce());
        }

        private async Task<ICollection> CreateAndValidate<T>(MappingServiceArgs args, Func<IDataSet, IDataRow, int, IEnumerable<T>> initFunc)
        {
            var hasError = true;
            var ret = new List<T>();

            for (var i = 0; i < args.Table.Rows.Count; i++)
            {
                var row = args.Table.Rows[i];
                if (row.ItemArray.Any(y => y != null))
                {
                    foreach (var item in initFunc(args.DataSet, row, i)?? Enumerable.Empty<T>())
                    {
                        if (item == null)
                            continue;
                        foreach (var validation in args.Validations)
                            hasError = await validation(item, new ValidationContext(item, args.ServiceProvider, args.ValidationCache)) && hasError;
                        ret.Add(item);
                    }
                   
                }
            }

            if (!hasError)
                args.Logger.LogError(string.Format(ValidationStageFailed, typeof(T).FullName));

            return ret;
        }

        protected record MappingServiceArgs(Type Type,
                                            IRowMapping RowMapping,
                                            ImmutableList<Func<object, ValidationContext, Task<bool>>> Validations,
                                            IServiceProvider ServiceProvider,
                                            Dictionary<object, object> ValidationCache,
                                            IDataSet DataSet,
                                            IDataTable Table,
                                            ILogger Logger,
                                            IEnumerable<string> IgnoredColumns,
                                            CancellationToken CancellationToken);
    }
}