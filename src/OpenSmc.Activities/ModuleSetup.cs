using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Activities;
using OpenSmc.ServiceProvider;

[assembly: ModuleSetup]
namespace OpenSmc.Activities;

public class ModuleSetup : Attribute, IModuleRegistry
{
    /// <remarks>this must not be a constant in order for module ordering to work properly</remarks>
    public static readonly string VariableName = "Activity";

    public void Register(IServiceCollection services)
    {
        services.AddTransient<IActivityService, ActivityService>();
    }
}