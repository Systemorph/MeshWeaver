using System.Collections;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.Contracts;
using System.Runtime.Serialization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenSmc.Activities;
using OpenSmc.Collections;
using OpenSmc.Data;
using OpenSmc.DataStructures;
using OpenSmc.Import.Builders;
using OpenSmc.Import.Options;
using OpenSmc.Messaging;
using OpenSmc.Reflection;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Import
{
    public class ImportPlugin(IMessageHub hub, ImportConfiguration configuration) : MessageHubPlugin<ImportState>(hub)
    {
        [Inject] private IActivityService activityService;
        [Inject] private IWorkspace workspace;
        public IMessageDelivery Execute(IMessageDelivery<ImportRequest> request)
        {
            ActivityLog log;
            activityService.Start(); 

            try
            {
                var import = request.Message;


                if(!configuration.DataSetReaders.TryGetValue(import.FileType, out var dataSetReader))
                    Fail($"File type {import.FileType} is unknown");

                var (format, dataSet) = dataSetReader.Invoke(import);

                //even if format was defined we take it from file
                format ??= import.Format;

                if (!configuration.ImportFormats.TryGetValue(format, out var importFormat))
                    return Fail($"Import format {format} is unknown.");

                importFormat.Import(import, dataSet);
                var dataSource = workspace.Context.GetDataSource(import.TargetDataSource ?? importFormat.TargetDataSource);

                var ret = new WorkspaceState(dataSource) ;

                foreach (var table in dataSet.Tables)
                {
                    if(importFormat.TableMappings.TryGetValue(table.TableName, out var tableMapping))
                        tableMapping.Map(dataSet, dataSet.Tables[tableMapping.TableName]);
                }


                    if (!activityService.HasErrors())
                    {
                        workspace.Commit();
                    }
                    else
                    {
                        workspace.Rollback();
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

                activityService.LogError(message.ToString());
            }
            finally
            {
                log = activityService.Finish();
            }

            return request.Processed();
        }

        private IMessageDelivery Fail(string s)
        {
            throw new NotImplementedException();
        }
#pragma warning disable 4014
        private static readonly IGenericMethodCache UpdateMethod =
            GenericCaches.GetMethodCacheStatic(() => PerformUpdate<object>(null, null));
#pragma warning disable 4014


        private static void PerformUpdate<T>(IWorkspace targetDataSource, ICollection items) where T : class
        {
            var options = new UpdateOptions();

            targetDataSource.Update((items as IEnumerable<T>)?.ToArray());
        }

        public static string ValidationStageFailed = "Validation stage of type {0} has failed.";



        protected ICollection PerformTypeActions<T>(ImportFormat format, TableMapping mapping)
            where T : class
        {
            if (mapping.RowMapping is ListRowMapping<T> listRowMapping)
                return CreateAndValidate(args, (ds, row, index) => listRowMapping.InitializeFunction(ds, row, index));

            var rowMapping = args.RowMapping as RowMapping<T>;
            var ignored = args.IgnoredColumns ?? Enumerable.Empty<string>();
            var columns = args.Table.Columns.Where(x => !ignored.Contains(x.ColumnName)).ToDictionary(x => x.ColumnName, x => x.Index);
            rowMapping = AutoMapper.AddAutoMapping(rowMapping, columns);

            return CreateAndValidate(args, (ds, row, index) => rowMapping.InitializeFunction(ds, row, index).RepeatOnce());
        }

        private ICollection CreateAndValidate<T>(IDataSet dataSet, IDataTable table,  ImportFormat format, Func<IDataSet, IDataRow, int, IEnumerable<T>> initFunc)
        {
            var hasError = true;
            var ret = new List<T>();

            for (var i = 0; i < table.Rows.Count; i++)
            {
                var row = table.Rows[i];
                if (row.ItemArray.Any(y => y != null))
                {
                    foreach (var item in initFunc(dataSet, row, i) ?? Enumerable.Empty<T>())
                    {
                        if (item == null)
                            continue;
                        foreach (var validation in format.Validations)
                            hasError = validation(item, new ValidationContext(item, hub.ServiceProvider, State.ValidationCache)) && hasError;
                        ret.Add(item);
                    }

                }
            }

            if (!hasError)
                activityService.LogError(string.Format(ValidationStageFailed, typeof(T).FullName));

            return ret;
        }

    }

    public record ImportState
    {
        public IDictionary<object, object> ValidationCache { get; set; }
    }
}