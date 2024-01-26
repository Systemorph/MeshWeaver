using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Application.Layout.Composition;
using OpenSmc.Application.Layout.DataBinding;
using OpenSmc.Application.Layout;
using OpenSmc.DotNet.Kernel;
using OpenSmc.Serialization;
using OpenSmc.ServiceProvider;

[assembly: InternalsVisibleTo("OpenSmc.Orsa.Messaging.Test")]

[assembly: ModuleSetup]
namespace OpenSmc.Application.Layout
{
    public class ModuleSetup : Attribute, IModuleRegistry, IModuleInitialization
    {
        public const string ModuleName = InteractivePresentation.ModuleName;
        public const string ApiVersion = InteractivePresentation.ApiVersion;
        public const string SmappWindow = nameof(SmappWindow);
        /// <summary>
        /// Id of top level UI control exposed as ILayout interface
        /// </summary>
        public const string TopLayout = nameof(TopLayout);

        public void Register(IServiceCollection services)
        {
            services.AddSingleton<IUiControlService, UiControlService>();
        }

        public void Initialize(IServiceProvider serviceProvider)
        {
            RegisterTypes(serviceProvider);

            DefaultUiControlRegistry.RegisterDefaults(serviceProvider.GetService<IUiControlService>());

            var kernel = serviceProvider.GetService<IDotNetKernel>();
            if (kernel != null)
                kernel.AddUsingType<DisplayAttribute>();
        }

        private static void RegisterTypes(IServiceProvider serviceProvider)
        {
            var eventRegistry = serviceProvider.GetService<IEventsRegistry>();
            eventRegistry.WithEvent<Binding>()
                         .WithEvent<SpinnerControl>()
                         .WithEvent<CheckBoxControl>();
        }
    }
}

public static class UiServiceCollectionExtensions
{
    public static void AddUiControlHub(this IServiceCollection services, Type generic)
    {
        services.AddTransient(generic);
    }
}

