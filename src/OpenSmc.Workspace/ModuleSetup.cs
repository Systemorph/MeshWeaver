using Microsoft.Extensions.DependencyInjection;
using OpenSmc.ServiceProvider;

[assembly:OpenSmc.Workspace.ModuleSetup]
namespace OpenSmc.Workspace
{
    public class ModuleSetup : Attribute, IModuleInitialization, IModuleRegistry
    {
        /// <remarks>this must not be a constant in order for module ordering to work properly</remarks>
        public static readonly string WorkspaceVariableName = nameof(Workspace);



        public void Register(IServiceCollection services)
        {
            services.AddTransient<IWorkspaceStorage, WorkspaceStorage>();
            services.AddTransient<IWorkspace, Workspace>();
            services.AddTransient<IWorkspaceVariable, WorkspaceVariable>();
        }

        public void Initialize(IServiceProvider serviceProvider)
        {
            throw new NotImplementedException();
        }
    }
}