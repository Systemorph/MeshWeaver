using Microsoft.Extensions.DependencyInjection;
using OpenSmc.DataSetReader.Excel.Utils;
using OpenSmc.DomainDesigner;
using OpenSmc.DomainDesigner.ExcelParser;
using OpenSmc.DomainDesigner.ExcelParser.DocumentParsing;
using OpenSmc.ServiceProvider;

[assembly: ModuleSetup]
namespace OpenSmc.DomainDesigner
{
    public class ModuleSetup : Attribute, IModuleRegistry, IModuleInitialization
    {
        public static readonly string VariableName = "Domain";

        public void Register(IServiceCollection services)
        {
            services.AddSingleton<IDomainDesignerVariable, DomainDesignerVariable>();
        }

        public void Initialize(IServiceProvider serviceProvider)
        {
            var domainProducerFactory = serviceProvider.GetService<IDomainDesignerVariable>();
            domainProducerFactory.RegisterParser("Excel", new ExcelStyleFormatParser(), convention => convention.Element(typeof(ExcelStyleFormatParser))
                                                                                                                .Condition()
                                                                                                                .IsFalse(x => x.GetExtensionByFileName() == ExcelExtensions.Excel10)
                                                                                                                .Delete());
            //TODO: Move also a session into the new repo
            //var sessionContext = serviceProvider.GetService<ISessionContext>();
            //if (sessionContext != null)
            //{
            //    var projectVariable = serviceProvider.GetService<IProjectFileStorage>();
            //    domainProducerFactory.SetDefaultFileStorage(projectVariable);
            //    sessionContext.SetVariable(VariableName, domainProducerFactory, typeof(IDomainDesignerVariable));
            //}
        }
    }
}