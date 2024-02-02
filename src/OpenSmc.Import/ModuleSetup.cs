using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Import;
using OpenSmc.Import.Contract.Mapping;
using OpenSmc.ServiceProvider;

[assembly:ModuleSetup]
namespace OpenSmc.Import;

public class ModuleSetup : Attribute, IModuleRegistry
{
    /// <remarks>this must not be a constant in order for module ordering to work properly</remarks>
    public static readonly string VariableName = "Import";

    public void Register(IServiceCollection services)
    {
        services.AddTransient<IMappingService, MappingService>();
        services.AddTransient<IImportVariable, ImportVariable>();
    }

}