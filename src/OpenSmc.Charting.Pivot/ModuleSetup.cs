//using System.ComponentModel.DataAnnotations;
//using OpenSmc.Charting.Pivot;
//using IModuleInitialization = Systemorph.ServiceProvider.IModuleInitialization;

//[assembly: ModuleSetup]

//namespace OpenSmc.Charting.Pivot;

//public class ModuleSetup : Attribute,IModuleInitialization
//{
//    public void Initialize(IServiceProvider serviceProvider)
//    {
//        var kernel = serviceProvider.GetService<IDotNetKernel>();
//        if (kernel != null)
//        {

//            kernel.AddUsingsStatic(typeof(PivotChartingExtensions).FullName,
//                                   typeof(PivotChartModelExtensions).FullName,
//                                   typeof(QueryableExtensions).FullName);

//            kernel.AddUsings(typeof(IQuerySource).Namespace,
//                             typeof(AggregateByAttribute).Namespace,
//                             typeof(DisplayAttribute).Namespace,
//                             typeof(NotVisibleAttribute).Namespace,
//                             typeof(IdentityPropertyAttribute).Namespace);
//        }
//    }
//}