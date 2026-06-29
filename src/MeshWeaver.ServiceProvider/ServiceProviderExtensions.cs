#nullable enable
using System.Reflection;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ServiceProvider;

/// <summary>
/// Extension methods that build and use an Autofac-backed <see cref="IServiceProvider"/> for
/// MeshWeaver, including child-scope creation and attribute-driven property/field injection.
/// </summary>
public static class ServiceProviderExtensions
{
    /// <summary>
    /// Builds a root MeshWeaver service provider from the given service collection, backed by Autofac.
    /// </summary>
    /// <param name="services">The service collection to populate the container with.</param>
    /// <returns>A new Autofac-backed <see cref="IServiceProvider"/> with no parent scope.</returns>
    public static IServiceProvider CreateMeshWeaverServiceProvider(this IServiceCollection services) =>
        SetupModules(services, null);

    /// <summary>
    /// Creates an Autofac-backed service provider for <paramref name="services"/>. When
    /// <paramref name="parent"/> is supplied, a child lifetime scope of the parent's Autofac
    /// container is begun (optionally tagged); otherwise a new root container is built.
    /// </summary>
    /// <param name="services">The service collection to populate; a new empty collection is used when null.</param>
    /// <param name="parent">The parent provider whose Autofac lifetime scope to nest under, or null for a new root container.</param>
    /// <param name="tag">An optional tag identifying the new child lifetime scope; ignored when <paramref name="parent"/> is null.</param>
    /// <returns>The resulting <see cref="IServiceProvider"/>.</returns>
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

    /// <summary>
    /// Populates all fields and properties of <paramref name="instance"/> that are marked with
    /// <see cref="InjectAttribute"/> by resolving each member's type from the service provider.
    /// </summary>
    /// <param name="serviceProvider">The provider used to resolve the injected dependencies.</param>
    /// <param name="instance">The object whose <see cref="InjectAttribute"/>-decorated members are filled in.</param>
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

    /// <summary>
    /// Creates an instance of <typeparamref name="T"/> using the service provider for constructor
    /// dependencies, with the supplied <paramref name="parameters"/> passed as explicit arguments.
    /// </summary>
    /// <typeparam name="T">The reference type to instantiate.</typeparam>
    /// <param name="provider">The provider used to resolve constructor dependencies not given explicitly.</param>
    /// <param name="parameters">Explicit constructor arguments to use during activation.</param>
    /// <returns>A newly created instance of <typeparamref name="T"/>.</returns>
    public static T ResolveWith<T>(this IServiceProvider provider, params object[] parameters)
        where T : class => ActivatorUtilities.CreateInstance<T>(provider, parameters);
}
