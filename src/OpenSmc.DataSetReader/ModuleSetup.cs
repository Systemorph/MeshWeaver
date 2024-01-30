using Microsoft.Extensions.DependencyInjection;
using OpenSmc.DataSetReader;
using OpenSmc.DataSetReader.Csv;
using OpenSmc.DataSetReader.Excel;
using OpenSmc.DataSetReader.Excel.Utils;
using OpenSmc.ServiceProvider;

[assembly: ModuleSetup]
namespace OpenSmc.DataSetReader
{
    public class ModuleSetup : Attribute, IModuleRegistry, IModuleInitialization
    {
        /// <remarks>this must not be a constant in order for module ordering to work properly</remarks>
        public static readonly string VariableName = "DataSetReader";

        public void Register(IServiceCollection services)
        {
            services.AddSingleton<IDataSetReadingService, DataSetReadingService>();
            services.AddSingleton<IDataSetReaderVariable, DataSetReaderVariable>();
        }

        public void Initialize(IServiceProvider serviceProvider)
        {
            var dataSetReadingService = serviceProvider.GetService<IDataSetReadingService>();
            dataSetReadingService.RegisterReader("Csv", new CsvDataSetReader(), convention => convention.Element(typeof(CsvDataSetReader))
                                                                                                          .AtEnd());
            dataSetReadingService.RegisterReader("Excel", new ExcelDataSetReader(), convention => convention.Element(typeof(ExcelDataSetReader))
                                                                                                            .Condition()
                                                                                                            .IsFalse(x => x == ExcelExtensions.Excel10)
                                                                                                            .Delete());
            dataSetReadingService.RegisterReader("Excel", new ExcelDataSetReader(), convention => convention.Element(typeof(ExcelDataSetReaderOld))
                                                                                                            .Condition()
                                                                                                            .IsFalse(x => x == ExcelExtensions.Excel03)
                                                                                                            .Delete());
        }
    }
}