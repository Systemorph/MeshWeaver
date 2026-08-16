using System.Reflection;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// One resolved cell-surface pack assembly for a kernel script session — the pack-scripting
/// seam's unit (issue #1649). A dynamic NodeType that declares <c>cellSurface: true</c> in its
/// definition opts its CURRENT baked assembly into the kernel's script surface; the provider
/// (implemented in <c>MeshWeaver.Graph</c>) resolves each such NodeType through the compilation
/// cache and hands the kernel this triple:
///
/// <list type="bullet">
///   <item><see cref="AssemblyPath"/> feeds the session's Roslyn METADATA reference (compile-time
///     visibility for bare-name calls in <c>--render</c>/executable cells);</item>
///   <item><see cref="Assembly"/> feeds the session load context's by-name RUNTIME bind — the
///     emitted submission references a <c>DynamicNode_*</c> identity that neither the session
///     context nor the Default ALC could otherwise resolve, because node assemblies live in
///     collectible per-NodeType load contexts;</item>
///   <item><see cref="Lease"/> pins that collectible generation for the session's lifetime.</item>
/// </list>
///
/// <para>🚨 Pinning semantics: holding <see cref="Assembly"/> + <see cref="Lease"/> keeps the
/// referenced collectible <c>NodeAssemblyLoadContext</c> generation alive until the lease is
/// disposed (the kernel disposes it with the session). Sessions are short-lived by design; a
/// NodeType recompile mid-session keeps old sessions bound to the OLD generation while new
/// sessions resolve the new one — the same generation semantics live layout areas already have.</para>
/// </summary>
/// <param name="NodeTypePath">The mesh path of the cell-surface NodeType (e.g. <c>Edu/TrainingSim</c>).</param>
/// <param name="AssemblyPath">The local PE path of the CURRENT baked assembly, for the metadata reference.</param>
/// <param name="Assembly">The loaded assembly in its (collectible) load context, for the runtime bind.</param>
/// <param name="Lease">Lifetime lease on the assembly's load context; disposed when the session dies.</param>
public sealed record CellSurfaceAssembly(
    string NodeTypePath,
    string AssemblyPath,
    Assembly Assembly,
    IDisposable Lease);

/// <summary>
/// The pack-scripting seam's resolution surface (issue #1649): resolves the CURRENT set of
/// cell-surface pack assemblies for one kernel session. Implemented in <c>MeshWeaver.Graph</c>
/// (<c>CellSurfaceAssemblyProvider</c> — the only project that can reach the NodeType catalog and
/// the compilation cache) and registered as a mesh-level singleton; the kernel resolves it
/// per session via DI, exactly like <see cref="KernelScriptAssembly"/> — which keeps the
/// dependency direction Graph → Kernel.Hub, not the reverse.
///
/// <para>PER-SESSION on purpose: the process-wide snapshot in <c>KernelScriptReferences</c> is
/// frozen at first materialization and can never see a pack assembly deterministically (NodeType
/// assemblies load lazily, recompile into fresh collectible contexts, and must be able to
/// unload). Resolving at session init makes the cell surface a declaration
/// (<c>cellSurface: true</c>) instead of a load-order lottery.</para>
/// </summary>
public interface ICellSurfaceAssemblyProvider
{
    /// <summary>
    /// Reactive: emits ONE current set of resolved cell-surface assemblies (empty when no
    /// NodeType opts in, or the resolution surface is unavailable) and completes. Each entry
    /// carries a live lease — the caller owns disposal (the kernel ties it to the session).
    /// </summary>
    IObservable<IReadOnlyCollection<CellSurfaceAssembly>> ResolveCellSurfaceAssemblies();
}
