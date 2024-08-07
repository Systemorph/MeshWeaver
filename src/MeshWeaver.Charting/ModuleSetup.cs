//using MeshWeaver.Charting;
//using MeshWeaver.Charting.Builders;
//using MeshWeaver.Charting.Enums;
//using MeshWeaver.Charting.Models;
//using IModuleInitialization = Systemorph.ServiceProvider.IModuleInitialization;

//[assembly:ModuleSetup]

//namespace MeshWeaver.Charting
//{
//    public class ModuleSetup : Attribute,IModuleInitialization, IModuleRegistry
//    {
//        /// <remarks>this must not be a constant in order for module ordering to work properly</remarks>
//        public static readonly string VariableName = "ChartBuilder";

//        public void Register(IServiceCollection services)
//        {
//            services.AddTransient<IChartBuilderVariable, ChartBuilderVariable>();
//        }

//        public void Initialize(IServiceProvider serviceProvider)
//        {
//            var uiControlService = serviceProvider.GetService<IUiControlService>();

//            uiControlService.Register<Chart>(instance => new ChartControl(instance));

//            var kernel = serviceProvider.GetService<IDotNetKernel>();
//            if (kernel != null)
//            {
//                kernel.AddUsings(typeof(TimeIntervals).Namespace);
//            }

//            var sessionContext = serviceProvider.GetService<ISessionContext>();
//            var chartBuilderVariable = serviceProvider.GetService<IChartBuilderVariable>();
//            if (sessionContext != null)
//            {
//                sessionContext.SetVariable(VariableName, chartBuilderVariable, typeof(IChartBuilderVariable));

//            }
//        }
//    }
//}