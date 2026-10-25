#nullable enable
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ServiceProvider;

/// <summary>
/// Extension methods that build and use MeshWeaver's <see cref="MeshServiceProvider"/>: the mesh's root
/// container, a hub's level under its parent, and attribute-driven property/field injection.
/// </summary>
public static class ServiceProviderExtensions
{
    /// <summary>
    /// Builds a root MeshWeaver service provider from the given service collection.
    /// </summary>
    /// <param name="services">The service collection to populate the container with.</param>
    /// <returns>A new <see cref="MeshServiceProvider"/> with no parent scope.</returns>
    public static IServiceProvider CreateMeshWeaverServiceProvider(this IServiceCollection services) =>
        SetupModules(services, null);

    /// <summary>
    /// Creates a service provider for <paramref name="services"/>. When <paramref name="parent"/> is
    /// supplied, a LEVEL of the parent's container is begun — a scope holding <paramref name="services"/>
    /// as its own registrations (frozen now, nothing copied from the parent), optionally tagged; otherwise
    /// a new root container is built.
    /// </summary>
    /// <param name="services">The service collection to populate; a new empty collection is used when null.</param>
    /// <param name="parent">The parent provider to nest under, or null for a new root container.</param>
    /// <param name="tag">An optional tag naming the new level; ignored when <paramref name="parent"/> is null.</param>
    /// <returns>The resulting <see cref="IServiceProvider"/>.</returns>
    public static IServiceProvider SetupModules(
        this IServiceCollection services,
        IServiceProvider? parent,
        string? tag = null
    )
    {
        services ??= new ServiceCollection();

        if (parent != null)
        {
            var scope = MeshServiceProvider.From(parent)
                        ?? throw new NotSupportedException("Mesh Weaver has not been properly configured.");
            // The parent's registry of module-owned instances (MeshWeaver#6588): a hub level's own
            // registrations are watched against the same owners as the root's.
            var owners = scope.GetService<ModuleInstanceOwners>();
            return scope.BeginScope(services.ToArray(), tag,
                owners is null ? null : [new OwnershipTransferObserver(owners)]);
        }

        var descriptors = services.ToArray();
        var rootOwners = descriptors.Select(d => d.IsKeyedService ? null : d.ImplementationInstance)
            .OfType<ModuleInstanceOwners>().LastOrDefault();
        // A ROOT container also takes every registration SOURCE registered as an instance — how a mesh's
        // module registry answers the types its modules declare (MeshWeaver.Mesh's ModuleOwnedRootSource).
        // Only the root: every hub's level asks its way up to it.
        var sources = descriptors
            .Where(d => d.ServiceType == typeof(IServiceRegistrationSource) && !d.IsKeyedService)
            .Select(d => d.ImplementationInstance)
            .OfType<IServiceRegistrationSource>()
            .ToArray();
        var root = MeshServiceProvider.Build(descriptors, sources,
            rootOwners is null ? null : [new OwnershipTransferObserver(rootOwners)]);
        // The ownership diagnostics and the late-disposal faults write through the root's own logging
        // (MeshWeaver#6584, #6588).
        var loggerFactory = root.GetService<Microsoft.Extensions.Logging.ILoggerFactory>();
        rootOwners?.UseLoggerFactory(loggerFactory);
        root.UseFaultLogger(loggerFactory is null ? null
            : loggerFactory.CreateLogger(typeof(MeshServiceProvider).FullName!));
        // Then every build callback registered as an instance, on THIS thread, before the provider is
        // handed to anyone — how a mesh's module registry builds its modules' shared instances before
        // they can be reached (MeshWeaver#6542).
        try
        {
            foreach (var callback in descriptors
                         .Where(d => d.ServiceType == typeof(IRootContainerBuilt) && !d.IsKeyedService)
                         .Select(d => d.ImplementationInstance)
                         .OfType<IRootContainerBuilt>()
                         .ToArray())
                callback.Built(root);
        }
        catch
        {
            // Nobody else holds this root: a callback that throws leaves no caller who could dispose
            // it, or the shared instances the callbacks before it already built.
            root.Dispose();
            throw;
        }
        return root;
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
