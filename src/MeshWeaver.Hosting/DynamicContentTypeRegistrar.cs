using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>What the registration-only pass did with one dynamic NodeType.</summary>
public enum ContentTypeRegistrationStatus
{
    /// <summary>The type's configuration ran on a transient probe and its content type now resolves here.</summary>
    Registered,

    /// <summary>This process already resolved the type's content type (an instance activated here first).</summary>
    AlreadyRegistered,

    /// <summary>The configuration ran and registered nothing — the type declares no content type.</summary>
    DeclaresNoContentType,

    /// <summary>
    /// The record claims no usable build for the live framework, so there is nothing to register
    /// from WITHOUT compiling — the first access compiles it, as before.
    /// </summary>
    NotBaked,

    /// <summary>The record claims a build but this process's assembly store has no bytes at that key.</summary>
    BytesMissing,

    /// <summary>
    /// The bytes on this replica are not the build the record published (MVID mismatch). Registering
    /// from them would bind a type family the activations will not use, so nothing is registered.
    /// </summary>
    StaleBytes,

    /// <summary>The recorded build did not load, or the probe faulted. Named in the detail; never fatal.</summary>
    Faulted,
}

/// <summary>One type's registration verdict.</summary>
/// <param name="TypePath">The NodeType's mesh path.</param>
/// <param name="Status">What happened.</param>
/// <param name="Detail">Why, for the non-obvious statuses.</param>
public sealed record ContentTypeRegistrationOutcome(
    string TypePath, ContentTypeRegistrationStatus Status, string? Detail = null);

/// <summary>
/// 🚨 <b>The registration-only pass over already-baked dynamic NodeTypes</b>
/// (Systemorph/MeshWeaver.Plugins#2180, <c>Doc/Architecture/DynamicContentTypeRegistration</c>).
///
/// <para>A dynamic NodeType's content CLR type enters <see cref="IMeshContentTypeRegistry"/> only as
/// a side effect of its hub configuration being BUILT, and that happens when an instance hub
/// cold-activates. A per-node hub is one activation cluster-wide, and the boot passes never build a
/// type they did not compile — the adopt-only probe asks and never builds, and the compiling sweep
/// reports a type the store already holds as <see cref="PreWarmStatus.AlreadyBaked"/> and never
/// activates it. So a type with FEW instances, all of them activated on another replica, is
/// untypeable here with a perfectly usable assembly: its pages render empty on this replica and not
/// on the other.</para>
///
/// <para><b>What this does, per type, and nothing more:</b> take the record's OWN claim of a usable
/// build for the live framework (<c>HasUsableBuild</c> — a pure record check), resolve those bytes in
/// the assembly store, load the configurations from the EXISTING assembly and run the type's
/// configuration once on a transient probe (<see cref="ContentTypeRegistration.ProbeRegister"/>) —
/// the build whose side effect is the registration. That is the enrichment hot path with every
/// write removed: no compile is driven, no NodeType record is written (no stale-Ok self-heal, no
/// <c>Pending</c> flip), no rebind watcher is armed. A type without a usable build, with missing
/// bytes or with bytes that are not the published build is SKIPPED and named — it is left to the
/// first access, exactly as before. That is why this may run on every replica, where the degrade
/// seam must not: a read seam taking the enrichment path would let every replica compile and
/// re-stamp one shared record.</para>
///
/// <para><b>When.</b> After the boot's bake barrier (<see cref="PreWarmCompletion"/>) settles — so no
/// probe meets an adopted-but-not-yet-loadable bundle, whose load failure trips the loader's
/// corrupt-file self-heal — and on a PACED trickle off the readiness path: the ~13.5 s of assembly
/// opening #1660 removed from boot is paid here, in the background, one type at a time. Each load
/// runs through the <see cref="IoPoolNames.FileSystem"/> <see cref="IIoPool"/> (it is blocking file
/// I/O plus reflection), as do the store lookup and the probe build, and the pause between types is
/// the sequence's own delay, so the pass never holds more than one pool slot at a time.</para>
/// </summary>
public static class DynamicContentTypeRegistrar
{
    /// <summary>Default pause between two types' registrations.</summary>
    public static readonly TimeSpan DefaultBetweenTypes = TimeSpan.FromMilliseconds(200);

    /// <summary>How long the one catalog enumeration may take before the pass gives up (and says so).</summary>
    private static readonly TimeSpan EnumerationBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Enumerates the mesh's dynamic NodeTypes (as system — infrastructure, not a user read) and
    /// registers the content type of every one that has a usable build here and is not registered
    /// yet. Emits one outcome per dynamic type, in path order, then completes. A fault of the
    /// ENUMERATION propagates; a fault of one type is that type's <see cref="ContentTypeRegistrationStatus.Faulted"/>
    /// outcome and never stops the pass.
    /// </summary>
    /// <param name="mesh">The mesh hub.</param>
    /// <param name="betweenTypes">The pause between two types that each did real work.</param>
    /// <param name="logger">Diagnostics.</param>
    public static IObservable<ContentTypeRegistrationOutcome> RegisterBakedTypes(
        IMessageHub mesh, TimeSpan betweenTypes, ILogger? logger = null)
    {
        var meshService = mesh.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Empty<ContentTypeRegistrationOutcome>();
        var accessService = mesh.ServiceProvider.GetService<AccessService>();
        return accessService.RunAsSystem(
                () => meshService
                    .Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.OfType(MeshNode.NodeTypePath)))
                    .Take(1)
                    .Timeout(EnumerationBudget))
            .SelectMany(change => RegisterTypes(
                mesh,
                DynamicTypePreWarmer.DynamicTypesOf(change.Items, mesh.JsonSerializerOptions, logger),
                betweenTypes,
                logger));
    }

    /// <summary>
    /// The pass over an enumeration already taken — the seam the tests drive, so the population is
    /// exactly the one under test.
    /// </summary>
    internal static IObservable<ContentTypeRegistrationOutcome> RegisterTypes(
        IMessageHub mesh,
        DynamicTypePreWarmer.DynamicTypes types,
        TimeSpan betweenTypes,
        ILogger? logger)
    {
        var registry = mesh.ServiceProvider.GetService<IMeshContentTypeRegistry>();
        var compilation = mesh.ServiceProvider.GetService<IMeshNodeCompilationService>();
        if (registry is null || compilation is null)
        {
            logger?.LogDebug(
                "DynamicContentTypeRegistrar: no content-type registry or compilation service on this "
                + "mesh — nothing to register");
            return Observable.Empty<ContentTypeRegistrationOutcome>();
        }
        var pool = mesh.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem)
                   ?? IoPool.Unbounded;
        var guards = NodeTypeCompilationHelpers.GuardsOf(mesh);

        var paths = types.Nodes.Keys.Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        var worked = 0;
        return paths
            .Select(path => Observable.Defer(() =>
            {
                var node = types.Nodes[path];
                types.Definitions.TryGetValue(path, out var def);
                if (registry.TryResolveByNodeType(path, out _))
                    return Observable.Return(new ContentTypeRegistrationOutcome(
                        path, ContentTypeRegistrationStatus.AlreadyRegistered));
                if (def is null || !NodeTypeCompilationHelpers.HasUsableBuild(node, def, guards))
                    return Observable.Return(new ContentTypeRegistrationOutcome(
                        path, ContentTypeRegistrationStatus.NotBaked,
                        "the record claims no usable build for this framework — the first access compiles it"));
                // Pace only between types that do real work: a skip opens nothing.
                var delay = Interlocked.Increment(ref worked) == 1 ? TimeSpan.Zero : betweenTypes;
                return RegisterOne(mesh, registry, compilation, pool, path, node, def, logger)
                    .DelaySubscription(delay);
            }))
            .Concat();
    }

    private static IObservable<ContentTypeRegistrationOutcome> RegisterOne(
        IMessageHub mesh,
        IMeshContentTypeRegistry registry,
        IMeshNodeCompilationService compilation,
        IIoPool pool,
        string path,
        MeshNode node,
        NodeTypeDefinition def,
        ILogger? logger)
    {
        var version = def.LastCompiledVersion ?? node.Version;
        var store = string.Equals(def.LatestAssemblyCollection, FrameworkAssemblyStore.CollectionName, StringComparison.Ordinal)
            ? (IAssemblyStore)FrameworkAssemblyStore.Instance
            : mesh.ServiceProvider.GetService<IAssemblyStore>() ?? NullAssemblyStore.Instance;

        // 🚨 The store lookup is file I/O too — FileSystemAssemblyStore probes the directory and
        // reads timestamps when SUBSCRIBED — so it runs inside the pool, never on the emitting thread.
        return pool.InvokeObservable(_ => store.TryGetAssemblyPath(path, version).Take(1))
            .SelectMany(localPath =>
            {
                if (string.IsNullOrEmpty(localPath))
                    return Observable.Return(new ContentTypeRegistrationOutcome(
                        path, ContentTypeRegistrationStatus.BytesMissing,
                        $"no bytes in this process's store for collection={def.LatestAssemblyCollection}, version={version}"));

                // The load is blocking file I/O plus reflection — it runs on the pool, and it is the
                // cost this pass moved off boot. GetConfigurationsFromExistingAssembly does its work
                // when CALLED and hands back an already-completed observable, so the call itself is
                // what must sit inside the pool.
                return pool
                    .InvokeBlocking(_ =>
                    {
                        var mismatch = ServedBuildIdentity.Mismatch(
                            def.LatestAssemblyMvid, ServedBuildIdentity.OfFile(localPath), path);
                        return (Mismatch: mismatch,
                            Load: mismatch is null
                                ? compilation.GetConfigurationsFromExistingAssembly(localPath, path)
                                : Observable.Return<NodeCompilationResult?>(null));
                    })
                    .SelectMany(step => step.Mismatch is { } stale
                        ? Observable.Return(new ContentTypeRegistrationOutcome(
                            path, ContentTypeRegistrationStatus.StaleBytes, stale))
                        // The probe build — a hosted hub's configuration and data-context build —
                        // is blocking work as well, so it takes its own pool slot.
                        : step.Load.Take(1).SelectMany(result =>
                            pool.InvokeBlocking(_ => Register(mesh, registry, path, result, logger))));
            })
            .Catch<ContentTypeRegistrationOutcome, Exception>(ex => Observable.Return(
                new ContentTypeRegistrationOutcome(
                    path, ContentTypeRegistrationStatus.Faulted, $"{ex.GetType().Name}: {ex.Message}")));
    }

    private static ContentTypeRegistrationOutcome Register(
        IMessageHub mesh,
        IMeshContentTypeRegistry registry,
        string path,
        NodeCompilationResult? result,
        ILogger? logger)
    {
        if (NodeTypeEnrichmentHelpers.UnloadableBuildDetail(result) is { } unloadable)
            return new ContentTypeRegistrationOutcome(path, ContentTypeRegistrationStatus.Faulted, unloadable);
        var config = result!.NodeTypeConfigurations
                         .FirstOrDefault(c => string.Equals(c.NodeType, path, StringComparison.OrdinalIgnoreCase))
                     ?? result.NodeTypeConfigurations.FirstOrDefault();
        if (config is null)
            return new ContentTypeRegistrationOutcome(
                path, ContentTypeRegistrationStatus.Faulted, "the recorded build carries no configuration");

        ContentTypeRegistration.ProbeRegister(mesh, path, config.HubConfiguration, logger);
        return registry.TryResolveByNodeType(path, out var registered)
            ? new ContentTypeRegistrationOutcome(path, ContentTypeRegistrationStatus.Registered, registered.FullName)
            : new ContentTypeRegistrationOutcome(path, ContentTypeRegistrationStatus.DeclaresNoContentType);
    }
}
