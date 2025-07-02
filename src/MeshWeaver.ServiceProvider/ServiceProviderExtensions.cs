#nullable enable
using System.Reflection;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ServiceProvider;

public static class ServiceProviderExtensions
{
    public static IServiceProvider CreateMeshWeaverServiceProvider(this IServiceCollection services) =>
        SetupModules(services, null);

    public static IServiceProvider SetupModules(
        this IServiceCollection services,
        IServiceProvider? parent,
        string? tag = null
    )
    {
        services ??= new ServiceCollection();

        IServiceProvider ret;
        if (parent != null)
        {
            var lifetimeScope = parent.GetService<ILifetimeScope>();
            if (lifetimeScope == null)
                throw new NotSupportedException("Mesh Weaver has not been properly configured.");
            else if (tag != null)
                ret = new AutofacServiceProvider(
                    lifetimeScope.BeginLifetimeScope(tag, c => c.Populate(services))
                );
            else
                ret = new AutofacServiceProvider(
                    lifetimeScope.BeginLifetimeScope(c => c.Populate(services))
                );
        }
        else
        {
            var containerBuilder = new ContainerBuilder();
            containerBuilder.Populate(services);

            ret = new AutofacServiceProvider(containerBuilder.Build());
        }
        return ret;
    }

    public static void Buildup(this IServiceProvider serviceProvider, object instance)
    {
        foreach (
            var fieldInfo in instance
                .GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(fi => fi.GetCustomAttributes().Any(x => x is InjectAttribute))
        )
            fieldInfo.SetValue(instance, serviceProvider.GetRequiredService(fieldInfo.FieldType));

        foreach (
            var propertyInfo in instance
                .GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(pi => pi.GetCustomAttributes().Any(x => x is InjectAttribute))
        )
            propertyInfo.SetValue(
                instance,
                serviceProvider.GetRequiredService(propertyInfo.PropertyType)
            );
    }

    public static T ResolveWith<T>(this IServiceProvider provider, params object[] parameters)
        where T : class => ActivatorUtilities.CreateInstance<T>(provider, parameters);
}
