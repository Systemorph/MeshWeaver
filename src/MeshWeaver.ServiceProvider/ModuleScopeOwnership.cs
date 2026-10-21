#nullable enable
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Autofac;
using Autofac.Core;
using Autofac.Core.Activators.Delegate;
using Autofac.Core.Lifetime;
using Autofac.Core.Resolving.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshWeaver.ServiceProvider;

/// <summary>
/// The instances a MODULE container built and owns, and who owns each — one registry per mesh, held by
/// the mesh's root container as an instance (never static). Read by the ownership diagnostics
/// (<c>Doc/Architecture/LiveModuleUpdate</c> → "Ownership diagnostics"): a scope that is about to dispose
/// one of these without owning it is the defect of MeshWeaver#6588.
///
/// <para>Holds its entries weakly (an ephemeron per instance): an entry lives exactly as long as the
/// instance it describes, so a retired module generation is never pinned by it.</para>
/// </summary>
public sealed class ModuleInstanceOwners
{
    private readonly ConditionalWeakTable<object, Owner> owners = new();
    private ImmutableHashSet<string> reported = ImmutableHashSet<string>.Empty;
    private ILogger logger = NullLogger.Instance;

    /// <summary>Where the ownership diagnostics are written — bound by the root container once it is built
    /// (<see cref="ServiceProviderExtensions.SetupModules"/>), so no diagnostic ever has to RESOLVE a logger
    /// in the middle of a resolve (in a module container that resolve goes through the module's fallback,
    /// and from there back into the very registration being diagnosed).</summary>
    /// <param name="factory">The root's logger factory, or null for none.</param>
    public void UseLoggerFactory(ILoggerFactory? factory) =>
        Volatile.Write(ref logger, ModuleScopeOwnership.LoggerFrom(factory));

    internal ILogger Logger => Volatile.Read(ref logger);

    /// <summary>Records that <paramref name="owner"/> owns <paramref name="instance"/> and is the only
    /// scope that may dispose it.</summary>
    /// <param name="instance">A disposable instance a module container built (or a proxy that forwards
    /// <c>Dispose</c> to one).</param>
    /// <param name="owner">The scope that disposes it, or null when no scope a caller can see does (the
    /// module's generation owns it).</param>
    /// <param name="ownerName">How the owner is named in a report.</param>
    public void Record(object instance, ILifetimeScope? owner, string ownerName)
    {
        ArgumentNullException.ThrowIfNull(instance);
        owners.AddOrUpdate(instance, new Owner(owner is null ? null : new WeakReference<ILifetimeScope>(owner), ownerName));
    }

    /// <summary>Whether <paramref name="instance"/> is a module-owned instance that
    /// <paramref name="scope"/> does not own; <paramref name="ownerName"/> names the owner when it is.</summary>
    internal bool IsOwnedByAnotherThan(object instance, ILifetimeScope scope, out string ownerName)
    {
        ownerName = "";
        if (!owners.TryGetValue(instance, out var owner))
            return false;
        if (owner.Scope is not null && owner.Scope.TryGetTarget(out var held) && ReferenceEquals(held, scope))
            return false;
        ownerName = owner.Name;
        return true;
    }

    /// <summary>True the first time <paramref name="key"/> is reported — what keeps a defect that repeats
    /// on every circuit from flooding the log.</summary>
    internal bool FirstReport(string key) => ImmutableInterlocked.Update(ref reported, set => set.Add(key));

    private sealed record Owner(WeakReference<ILifetimeScope>? Scope, string Name);
}

/// <summary>
/// 🚨 <b>Ownership diagnostics</b> for module services (MeshWeaver#6584, MeshWeaver#6588). Both defects
/// wrote NO log line while they were live, so a quiet production log could never tell "fixed" from
/// "never logs". These are the lines the defects WOULD write: each fires only when the defect is present —
/// nothing on the happy path — at <see cref="LogLevel.Warning"/>, once per registration, under the
/// category <see cref="Category"/> and the stable prefix <see cref="Marker"/>.
/// </summary>
public static class ModuleScopeOwnership
{
    /// <summary>The logger category every ownership diagnostic is written under.</summary>
    public const string Category = "MeshWeaver.ServiceProvider.ModuleScopeOwnership";

    /// <summary>The stable prefix of every ownership diagnostic — what a log query matches.</summary>
    public const string Marker = "[ModuleScopeOwnership]";

    /// <summary>
    /// MeshWeaver#6588: watches every registration <paramref name="builder"/> makes that hands out a
    /// FACTORY's result owned by the resolving scope. When such a factory returns an instance a module
    /// owns (<see cref="ModuleInstanceOwners"/>), the resolving scope will dispose it when it ends — under
    /// the owner — and the diagnostic is written as that scope begins to end. Reflection-activated and
    /// provided-instance registrations build or hold their own instance, so they cannot transfer
    /// ownership and are not watched; neither is a registration that is externally owned.
    /// </summary>
    /// <param name="builder">A container builder of a MeshWeaver container or scope.</param>
    /// <param name="owners">The mesh's registry — captured here, never resolved during a resolve.</param>
    internal static void WatchOwnershipTransfers(ContainerBuilder builder, ModuleInstanceOwners owners) =>
        builder.ComponentRegistryBuilder.Registered += (_, e) =>
        {
            var registration = e.ComponentRegistration;
            if (registration.Ownership != InstanceOwnership.OwnedByLifetimeScope
                || registration.Activator is not DelegateActivator)
                return;
            registration.PipelineBuilding += (_, pipeline) =>
                pipeline.Use(PipelinePhase.Activation, MiddlewareInsertionMode.StartOfPhase, (context, next) =>
                {
                    next(context);
                    if (context.NewInstanceActivated && context.Instance is { } instance
                        && instance is IDisposable or IAsyncDisposable)
                        CheckTransfer(owners, context, instance);
                });
        };

    private static void CheckTransfer(ModuleInstanceOwners owners, ResolveRequestContext context, object instance)
    {
        var scope = context.ActivationScope;
        if (!owners.IsOwnedByAnotherThan(instance, scope, out var ownerName))
            return;
        var service = context.Service.Description;
        if (!owners.FirstReport($"6588|{context.Registration.Id}|{ownerName}"))
            return;
        var logger = owners.Logger;
        var instanceType = instance.GetType().FullName;
        var scopeTag = scope.Tag;
        scope.CurrentScopeEnding += (_, _) => logger.LogWarning(
            "{Marker} Scope {Scope} is ending and will dispose {Service} ({Instance}), which {Owner} owns — "
            + "a registration owned by the resolving scope returned a module-owned instance, so a scope that "
            + "does not own it disposes it under its owner and every other user of it (MeshWeaver#6588). "
            + "Reported once per registration and owner.",
            Marker, scopeTag, service, instanceType, ownerName);
    }

    internal static ILogger LoggerFrom(ILoggerFactory? factory) =>
        factory?.CreateLogger(Category) ?? NullLogger.Instance;
}

/// <summary>
/// The ownership diagnostics of ONE module container (MeshWeaver#6584; see
/// <see cref="ModuleScopeOwnership"/>): records who owns each shared instance it builds, and reports a
/// SCOPED instance that no caller owns, or one served to a caller that does not own it. Held by the
/// container as an instance, never static; its entries are weak.
/// </summary>
internal sealed class ModuleScopeDiagnostics(ILogger logger, ModuleInstanceOwners? owners)
{
    private readonly ConditionalWeakTable<object, ScopedOwner> scoped = new();
    private ImmutableHashSet<string> reported = ImmutableHashSet<string>.Empty;

    /// <summary>Watches every shared registration of the module container <paramref name="builder"/>
    /// builds, except the container's own infrastructure.</summary>
    public void Watch(ContainerBuilder builder) =>
        builder.ComponentRegistryBuilder.Registered += (_, e) =>
        {
            var registration = e.ComponentRegistration;
            if (registration.Sharing != InstanceSharing.Shared
                || registration.Ownership != InstanceOwnership.OwnedByLifetimeScope
                || IsInfrastructure(registration.Activator.LimitType))
                return;
            registration.PipelineBuilding += (_, pipeline) =>
                pipeline.Use(PipelinePhase.Activation, MiddlewareInsertionMode.StartOfPhase, (context, next) =>
                {
                    next(context);
                    if (context.NewInstanceActivated && context.Instance is { } instance)
                        Built(context.Registration, context.Service, instance, context.ActivationScope);
                });
        };

    private static bool IsInfrastructure(Type type) =>
        type.Assembly == typeof(ModuleScopeDiagnostics).Assembly
        || type.Assembly.GetName().Name?.StartsWith("Autofac", StringComparison.Ordinal) == true;

    private void Built(IComponentRegistration registration, Service service, object instance, ILifetimeScope scope)
    {
        var caller = ModuleServiceProvider.CallerOfModuleScope(scope);
        if (instance is IDisposable or IAsyncDisposable)
            owners?.Record(instance, scope, caller is null ? $"module scope '{scope.Tag}'" : $"the module scope owned by '{caller.Tag}'");
        if (registration.Lifetime is not CurrentScopeLifetime)
            return;
        scoped.AddOrUpdate(instance, new ScopedOwner(caller is null ? null : new WeakReference<ILifetimeScope>(caller), caller?.Tag?.ToString()));
        // The defect's own shape: a SCOPED registration built in the module container's ROOT scope is one
        // instance per generation, shared by every circuit, request and hub, and built against no caller.
        if (caller is null && scope is ISharingLifetimeScope sharing && ReferenceEquals(sharing.RootLifetimeScope, sharing)
            && FirstReport($"root|{registration.Id}"))
            logger.LogWarning(
                "{Marker} Module scoped service {Service} ({Instance}) was built in the module container's root "
                + "scope, which no caller owns — every circuit, request and hub resolving it there shares this one "
                + "instance, built against no caller's NavigationManager or IJSRuntime (MeshWeaver#6584). "
                + "Reported once per registration.",
                ModuleScopeOwnership.Marker, service.Description, instance.GetType().FullName);
    }

    /// <summary>Reports <paramref name="instance"/>, a module's scoped instance, when it is handed to
    /// <paramref name="caller"/> although another caller (or none) owns the module scope it was built in.</summary>
    public void ServedTo(object instance, ILifetimeScope caller)
    {
        if (!scoped.TryGetValue(instance, out var owner))
            return;
        if (owner.Caller is not null && owner.Caller.TryGetTarget(out var held) && ReferenceEquals(held, caller))
            return;
        if (!FirstReport($"served|{instance.GetType().FullName}"))
            return;
        logger.LogWarning(
            "{Marker} Module scoped service {Instance} built in the module scope owned by {Owner} was served to "
            + "{Caller}, which does not own that scope — one caller is sharing another's instance (MeshWeaver#6584). "
            + "Reported once per service type.",
            ModuleScopeOwnership.Marker, instance.GetType().FullName, owner.Tag ?? "no caller", caller.Tag);
    }

    private bool FirstReport(string key) => ImmutableInterlocked.Update(ref reported, set => set.Add(key));

    private sealed record ScopedOwner(WeakReference<ILifetimeScope>? Caller, string? Tag);
}
