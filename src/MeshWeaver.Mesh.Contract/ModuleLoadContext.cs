using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using MeshWeaver.Messaging;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.ServiceProvider;

namespace MeshWeaver.Mesh;

/// <summary>
/// The collectible <see cref="AssemblyLoadContext"/> ONE generation of ONE module runs in — the
/// unit a live module update swaps (policy <c>module-live-update-default</c>,
/// <c>Doc/Architecture/LiveModuleUpdate</c>).
///
/// <para><b>Resolution order, and why each step is where it is.</b></para>
/// <list type="number">
/// <item><b>The platform.</b> Any name the DEFAULT context can bind — every platform contract,
/// <c>MeshWeaver.*</c> included, and the application closure — is served from there, whatever copy
/// the module's own directory may carry. So there is exactly one <c>MeshNodeProviderAttribute</c>,
/// one <c>IMessageHub</c>, one <c>MeshNode</c> in the process, and every contribution a module
/// makes crosses the boundary with its identity intact. A bundled platform copy is never used.
/// The default context serves a requested version at or below its own and refuses a higher one —
/// the floor-not-met case, which then falls through to the module's own copy exactly as a NodeType
/// build does (<c>NodeAssemblyLoadContext.Load</c>, policy
/// <c>platform-backwards-compatibility</c>).</item>
/// <item><b>Another module.</b> A name that is the ENTRY assembly of a module currently installed
/// (<see cref="ModuleContexts"/>) is served from that module's current generation, and a name a
/// module this one depends on has already loaded is served from that module's context — so two
/// modules that share a dependency share its identity along the dependency edge, the way they did
/// when every module sat in the default context. Each such edge is recorded
/// (<see cref="DependsOn"/>): it is what makes a module's dependents part of its swap.</item>
/// <item><b>The module's own generation directory.</b> Its private closure.</item>
/// </list>
///
/// <para>🚨 <b>Never a <c>Default.Resolving</c> handler that hands out a module assembly.</b> The
/// default context caches a binding for the life of the process, so the first generation it saw
/// would be pinned forever and no swap could ever take effect — and a non-collectible assembly
/// binding a collectible one is refused by the runtime anyway. Contexts that need a module
/// (NodeType builds) ask <see cref="ModuleContexts.Resolve"/> explicitly; a kernel script session
/// asks <see cref="ModuleContexts.ResolveDependency"/>, which also binds a module's PRIVATE
/// dependencies through this context.</para>
///
/// <para>Marked <see cref="IPlatformLoadContext"/>: a module is first-party compiled code that
/// shipped through the module lane, not code the mesh compiled at runtime, so the in-mesh
/// impersonation guard classifies it with the platform — and the marker is authentic because this
/// type lives in an assembly the default context loaded.</para>
/// </summary>
public sealed class ModuleLoadContext : AssemblyLoadContext, IPlatformLoadContext
{
    /// <summary>The prefix every module context's name carries: <c>module:&lt;Name&gt;#&lt;sequence&gt;</c>.</summary>
    public const string NamePrefix = "module:";

    private readonly ModuleContexts owner;
    private readonly object gate = new();
    private ImmutableHashSet<string> dependsOn = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);

    internal ModuleLoadContext(ModuleContexts owner, string moduleName, string directory, long sequence,
        ModuleSwapStage? stage = null)
        : base($"{NamePrefix}{moduleName}#{sequence}", isCollectible: true)
    {
        this.owner = owner;
        Stage = stage;
        ModuleName = moduleName;
        Directory = directory;
        // The same two process-static caches a NodeType context purges (CompilationCacheService):
        // each would otherwise root this context from a static field the moment one of its types
        // was resolved through Autofac or serialised through System.Text.Json, and a context that
        // can never be collected is a swap that never finishes. Static handlers — no self-reference.
        Unloading += ReflectionCacheEviction.EvictFor;
        Unloading += JsonMemberAccessorCacheEviction.EvictFor;
    }

    /// <summary>The live swap this context was loaded into, or null; see <see cref="ModuleSwapStage"/>.</summary>
    internal ModuleSwapStage? Stage { get; }

    /// <summary>The module's entry-assembly simple name.</summary>
    public string ModuleName { get; }

    /// <summary>The generation directory this context loads the module's private closure from.</summary>
    public string Directory { get; }

    /// <summary>
    /// The modules this generation has bound to through the registry — by entry-assembly name.
    /// Grows as the module's code first touches each one; a swap of any of them must take this
    /// generation with it.
    /// </summary>
    public ImmutableHashSet<string> DependsOn
    {
        get { lock (gate) return dependsOn; }
    }

    // The retirement sentinel (ModuleContexts.RetireCore): referenced ONLY from here, so it becomes
    // unreachable in the same collection as this context and its finalizer reports the unload done.
    private object? retirementSentinel;

    internal void HoldRetirementSentinel(object sentinel) =>
        Interlocked.CompareExchange(ref retirementSentinel, sentinel, null);

    internal void RecordDependency(string moduleName)
    {
        lock (gate)
            dependsOn = dependsOn.Add(moduleName);
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { Length: > 0 } name)
            return null;

        // 1. The platform — resolved, not merely "already loaded" (see ModulesAssemblyLoadContext,
        // #3662): a platform assembly nothing has touched yet must still win over a bundled copy.
        try
        {
            return Default.LoadFromAssemblyName(assemblyName);
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            // Not the platform's — another module's, or this module's own.
        }

        // 2. Another module, or a dependency one of the modules this one depends on already holds.
        if (owner.ResolveForDependent(name, this) is { } fromModule)
            return fromModule;

        // 3. This generation's own closure.
        var candidate = Path.Combine(Directory, name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }

    /// <inheritdoc />
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        // The same candidates, in the same order, the default context's module hook probes
        // (ModuleNativeAssets, #1728) — so a module's runtimes/<rid>/native payload resolves the same
        // way whichever context it runs in.
        foreach (var candidate in ModuleNativeAssets.CandidatePaths(
                     Directory, unmanagedDllName, RuntimeInformation.RuntimeIdentifier))
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        return IntPtr.Zero;
    }
}
