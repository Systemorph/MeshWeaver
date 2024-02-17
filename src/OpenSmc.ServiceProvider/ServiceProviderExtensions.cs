using System.Reflection;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace OpenSmc.ServiceProvider;

public static class ServiceProviderExtensions
{

    public static IServiceProvider SetupModules(this IServiceCollection services, ModulesBuilder modulesBuilder) => SetupModules(services, null, modulesBuilder);
    public static IServiceProvider SetupModules(this IServiceCollection services, IServiceProvider parent, ModulesBuilder modulesBuilder, string tag = null)
    {
        Modules modules = modulesBuilder.Build();

        services ??= new ServiceCollection();

        LoadedModulesService loadedModulesService = parent?.GetService<LoadedModulesService>();
        if (loadedModulesService == null)
        {
            loadedModulesService = new LoadedModulesService();
            services.AddSingleton(loadedModulesService);
        }

        foreach (var moduleRegistry in modules.Registries.Where(x => !loadedModulesService.Types.Contains(x.GetType())))
            moduleRegistry.Register(services);


        IServiceProvider ret;
        if (parent != null)
        {
            var lifetimeScope = parent.GetService<ILifetimeScope>();
            if (lifetimeScope == null)
                throw new InvalidOperationException($"Parent service provider must be created by {nameof(SetupModules)} or hostBuilder.ConfigureModules");
            if (tag != null)
                ret = new AutofacServiceProvider(lifetimeScope.BeginLifetimeScope(tag, c => c.Populate(services)));
            else
                ret = new AutofacServiceProvider(lifetimeScope.BeginLifetimeScope(c => c.Populate(services)));
        }
        else
        {
            var containerBuilder = new ContainerBuilder();
            containerBuilder.Populate(services);

            ret = new AutofacServiceProvider(containerBuilder.Build());
        }

        var loadedModulesCopy = loadedModulesService.Types.ToHashSet();

        // must be called before module.AddScopesDataCubes() to exclude double registration/initialization
        loadedModulesService.Types.UnionWith(modules.Registries.Select(x => x.GetType()));
        loadedModulesService.Types.UnionWith(modules.Initializations.Select(x => x.GetType()));


        foreach (var moduleInitialization in modules.Initializations.Where(x => !loadedModulesCopy.Contains(x.GetType())))
            moduleInitialization.AddScopesDataCubes(ret);


        return ret;

    }

    public static void Buildup(this IServiceProvider serviceProvider, object instance)
    {
        foreach (var fieldInfo in instance.GetType()
                                         .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                         .Where(fi => fi.GetCustomAttributes().Any(x => x is InjectAttribute)))
            fieldInfo.SetValue(instance, serviceProvider.GetRequiredService(fieldInfo.FieldType));

        foreach (var propertyInfo in instance.GetType()
                                            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                            .Where(pi => pi.GetCustomAttributes().Any(x => x is InjectAttribute)))
            propertyInfo.SetValue(instance, serviceProvider.GetRequiredService(propertyInfo.PropertyType));

    }

    public static T ResolveWith<T>(this IServiceProvider provider, params object[] parameters) where T : class =>
        ActivatorUtilities.CreateInstance<T>(provider, parameters);

}