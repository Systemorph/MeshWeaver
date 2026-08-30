using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// Process-shared, materialized-once Roslyn metadata references for kernel script
/// sessions.
///
/// <para><b>Why this exists (the ~200 MiB-per-session native leak):</b> every kernel
/// session compiles its submissions against ~350 assemblies. Left to itself, Roslyn
/// materializes a fresh <c>AssemblyMetadata</c> → <c>MetadataReader</c> → native
/// metadata block for EVERY one of them PER SESSION — via two paths: (1) raw
/// <see cref="Assembly"/> objects passed to <c>ScriptOptions.WithReferences</c>, and
/// (2) <c>RuntimeMetadataReferenceResolver.ResolveMissingAssembly</c>, which the
/// script compilation calls for the whole transitive closure of the globals type
/// (<c>MeshScriptGlobals</c> pulls in the full MeshWeaver graph). The metadata
/// section of a managed PE is copied into a NATIVE heap block — Microsoft.Graph.dll
/// alone is a 41 MiB block — and Roslyn's script <c>LoadContext</c> is
/// non-collectible, so nothing is ever reclaimed. A full-dump analysis of one
/// kernel-test class run showed 2,073+ live <c>MetadataReader</c>s (≈ sessions ×
/// refs), five identical 41 MiB Microsoft.Graph metadata blocks (one per session),
/// and ~1.1 GiB of committed private native memory — the direct cause of the CI
/// memory-pressure flakes (shifting observable-timeout failures late in every
/// shard).</para>
///
/// <para><b>The fix:</b> one <see cref="PortableExecutableReference"/> per assembly
/// file, once per process, shared by every session — both for the declared
/// reference set (<see cref="GetReferencesAsync"/>) and for resolver-driven resolution
/// (<see cref="SharedScriptMetadataResolver"/> wraps
/// <see cref="ScriptMetadataResolver.Default"/> and memoizes per path). Roslyn
/// shares the underlying <c>AssemblyMetadata</c> across compilations that use the
/// same reference instance, so the per-session metadata cost drops to ~zero.</para>
///
/// <para><b>NoStaticState.md compliance:</b> <see cref="Materialized"/> is a
/// process-global MEMO — pure-by-key (absolute file path → immutable reference over
/// immutable on-disk bytes), bounded by the set of assemblies on disk, and holding
/// NO <see cref="Type"/>s and NO AssemblyLoadContexts — it can pin neither meshes
/// nor collectible NodeType contexts. Allowlisted in <c>NoStaticCollectionsTest</c>
/// next to the other MEMO entries.</para>
/// </summary>
internal static class KernelScriptReferences
{
    /// <summary>
    /// MEMO: absolute assembly file path → the ONE shared
    /// <see cref="PortableExecutableReference"/> (and thus the one native metadata
    /// materialization) for that file in this process.
    /// </summary>
    private static readonly ConcurrentDictionary<string, PortableExecutableReference> Materialized =
        new(StringComparer.OrdinalIgnoreCase);

    // 🚨 Task, not a bare value — see GetReferencesAsync. The materialization below is
    // synchronous, uncancellable (no CancellationToken overload exists for
    // MetadataReference.CreateFromFile) CPU/disk work over ~350 assemblies, run via Task.Run so
    // it executes on its OWN detached ThreadPool work item, decoupled from whichever caller's
    // pooled leaf happens to trigger the FIRST-ever build. A caller's own cancellation must never
    // abort this — it is shared by every mesh in the process — so GetReferencesAsync races the
    // CALLER's token against this shared Task with Task.WaitAsync, never against the work itself.
    //
    // 🚨 IT IS A WARM-UP, NOT THE ANSWER (#2616). It used to be the answer: a
    // Lazy<Task<ImmutableArray<...>>> whose value GetReferencesAsync returned directly. That
    // FROZE `AppDomain.CurrentDomain.GetAssemblies()` as of the process's FIRST kernel session —
    // and assemblies load LAZILY, so which ones existed at that instant was a load-order lottery.
    // Whatever had not loaded yet was missing from every script compilation for the life of the
    // process: completions silently short a symbol, and a script referencing it fails to compile.
    // Under parallel shard load the lottery is decided by whichever unrelated test ran first,
    // which is why it presented as a flake reproducing on completely unrelated diffs
    // (ScriptCompletions_FilterByTypedPrefix_NotJustTheAlphabet losing "Mesh").
    // So this Task now only WARMS the memo — the expensive first materialization still happens
    // once, off the caller's pooled leaf — and GetReferencesAsync composes the set fresh from the
    // CURRENT assembly list on every call. Later calls are dictionary hits, so the cost of being
    // correct is an array copy and ~350 lookups, against a Roslyn compilation.
    private static readonly Lazy<Task> SharedWarmup =
        new(() => Task.Run(() => MaterializeCurrentAssemblies()), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Every non-collectible, production-referenceable assembly loaded RIGHT NOW, as shared
    /// references. Called on each <see cref="GetReferencesAsync"/> so a late-loading assembly is
    /// never invisible; the per-file memo makes every call after the first a dictionary hit.
    /// </summary>
    private static ImmutableArray<PortableExecutableReference> MaterializeCurrentAssemblies()
        => AppDomain.CurrentDomain.GetAssemblies()
            .Where(IsProductionReferenceable)
            // 🚨 Collectible-ALC assemblies (DynamicNode_* NodeType builds, other kernel
            // sessions) NEVER enter the frozen snapshot. They are per-GENERATION: every
            // recompile mints a fresh collectible context, so a snapshot entry is stale the
            // moment its type recompiles — and once the cell-surface seam (#1649) adds the
            // CURRENT generation per session, a frozen older generation of the SAME assembly
            // name would make every bare-name call ambiguous (CS0433 between two generations).
            // Excluding them also kills the load-order lottery the issue documents: a pack
            // assembly was cell-visible only if it happened to load before the process's first
            // kernel session. Pack types join the surface exactly one way now — the explicit
            // per-session `cellSurface: true` opt-in; modules join per-session via
            // MeshScriptEnvironment.SessionAssemblies (Default ALC, so unaffected here).
            .Where(asm => AssemblyLoadContext.GetLoadContext(asm)?.IsCollectible != true)
            .Select(TryGetOrCreate)
            .Where(r => r is not null)
            .Select(r => r!)
            .Distinct()
            .ToImmutableArray();

    /// <summary>
    /// 🚨 The script reference set must be the set a PRODUCTION portal would have —
    /// never "whatever this process happens to have loaded".
    ///
    /// <para><b>The defect this closes (2026-08-12, exports dead in prod):</b> the
    /// reference set is built from <see cref="AppDomain.CurrentDomain"/>, and a TEST
    /// process always has <c>MeshWeaver.Fixture</c> loaded (<c>MonolithMeshTestBase</c>
    /// derives from <c>Fixture.TestBase</c>). That assembly declares, in namespace
    /// <c>MeshWeaver.Mesh</c> — which <see cref="MeshScriptEnvironment.Imports"/> puts
    /// in scope for every script — the test-only <c>IMeshService.QueryAsync&lt;T&gt;</c>
    /// bridge whose own doc comment says <i>"Production code MUST NOT use these"</i>.
    /// So the three export <c>.csx</c> templates bound to it, compiled green in ELEVEN
    /// tests that genuinely execute them, and threw CS1061 on the first real export in
    /// production. Test compilation was strictly MORE permissive than production, which
    /// makes those tests unable to gate the thing they exist to gate.</para>
    ///
    /// <para>Excluding test scaffolding here makes script compilation in a test process
    /// IDENTICAL to production, so every existing script test becomes a real gate for
    /// dead-API references. This enforces a contract the Fixture already states about
    /// itself; it is not a heuristic about what scripts "should" use.</para>
    ///
    /// <para>Note this bounds only the <b>script</b> reference set. Test assemblies keep
    /// their ordinary project references, so test C# still calls the Fixture bridges
    /// normally — it is only script text that loses the production-shadowing surface.</para>
    /// </summary>
    private static bool IsProductionReferenceable(Assembly asm)
    {
        var name = asm.GetName().Name;
        if (string.IsNullOrEmpty(name)) return true;
        return !IsTestScaffolding(name);
    }

    private static bool IsTestScaffolding(string assemblyName)
        => MeshScriptEnvironment.IsTestScaffolding(assemblyName);

    /// <summary>
    /// The shared reference for <paramref name="asm"/>, or null when the assembly
    /// cannot be referenced (dynamic, byte-loaded, or its file was deleted —
    /// collectible NodeType ALCs leave Assembly objects in AppDomain after a test
    /// removes their cache directory; CreateFromFile on a missing path would throw
    /// and must not poison the whole set).
    /// </summary>
    private static PortableExecutableReference? TryGetOrCreate(Assembly asm)
    {
        if (asm.IsDynamic) return null;
        var location = asm.Location;
        if (string.IsNullOrEmpty(location)) return null;
        return TryGetOrCreate(location);
    }

    private static PortableExecutableReference? TryGetOrCreate(string location)
    {
        if (Materialized.TryGetValue(location, out var existing))
            return existing;
        try
        {
            if (!File.Exists(location)) return null;
            // GetOrAdd with a freshly created value: a concurrent racer's loser copy
            // is dropped and collected — at most a transient double-materialization,
            // never a leak.
            return Materialized.GetOrAdd(location, MetadataReference.CreateFromFile(location));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Re-keys a resolver-produced reference onto the shared materialization for
    /// its file path. References without a path (in-memory) or with non-default
    /// properties (aliases, embed-interop) pass through untouched — sharing an
    /// instance across different properties would change compilation semantics.
    /// </summary>
    public static PortableExecutableReference? Share(PortableExecutableReference? reference)
    {
        if (reference?.FilePath is not { Length: > 0 } path)
            return reference;
        if (reference.Properties != MetadataReferenceProperties.Assembly)
            return reference;
        return TryGetOrCreate(path) ?? reference;
    }

    /// <summary>
    /// References for one kernel session: the process-shared snapshot plus shared
    /// references for any assembly in <paramref name="sessionAssemblies"/> (curated
    /// core set + DI-contributed module assemblies) that was loaded after the
    /// snapshot was taken. The per-session cost is ~zero once the snapshot is warm —
    /// everything resolves to the same process-wide instances.
    ///
    /// <para>🚨 <paramref name="ct"/> governs only how long THIS caller waits — it is raced
    /// against the shared snapshot Task via <c>Task.WaitAsync</c>, never used to cancel the
    /// snapshot build itself. The build is a process-wide, cache-forever memo shared by every
    /// mesh in the process (issues #2480 /
    /// #2578: it used to run inline inside a per-request pooled leaf with no cancellation
    /// participation at all — the FIRST caller in the process paid its full, sometimes
    /// multi-second cold cost while holding that leaf's IIoPool gate permit, and a silo/mesh
    /// teardown racing that first caller had no way to reclaim the permit within the drain
    /// budget). Racing the WAIT (not the work) means: this caller's own pooled leaf settles
    /// promptly on cancellation and releases its gate permit, while the shared build keeps
    /// running to completion in the background for whichever mesh/request needs it next — safe,
    /// because it never touches a collectible NodeType ALC or any one mesh's disposed DI scope
    /// (<see cref="MaterializeCurrentAssemblies"/> explicitly excludes collectible-ALC assemblies).</para>
    /// </summary>
    public static async Task<ImmutableArray<MetadataReference>> GetReferencesAsync(
        IEnumerable<Assembly> sessionAssemblies, CancellationToken ct)
    {
        // Wait for the shared warm-up (never for the caller's own materialization), then build the
        // set from what is loaded NOW — see SharedWarmup for why this must not be a frozen list.
        await SharedWarmup.Value.WaitAsync(ct).ConfigureAwait(false);
        var snapshot = MaterializeCurrentAssemblies();
        var result = ImmutableArray.CreateBuilder<MetadataReference>(snapshot.Length + 4);
        var seen = new HashSet<PortableExecutableReference>();
        foreach (var reference in snapshot)
        {
            if (seen.Add(reference))
                result.Add(reference);
        }
        foreach (var asm in sessionAssemblies)
        {
            // Same production-parity gate as the snapshot — a DI-contributed module
            // assembly in a test process must not widen the script surface either.
            if (!IsProductionReferenceable(asm)) continue;
            var reference = TryGetOrCreate(asm);
            if (reference is not null && seen.Add(reference))
                result.Add(reference);
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// Shared reference for an explicit file path (the <c>#r "nuget: …"</c> restore
    /// path) — same memo, so repeated cells / sessions don't re-materialize package
    /// metadata either.
    /// </summary>
    public static PortableExecutableReference? GetOrCreateFromFile(string path)
        => TryGetOrCreate(path);

    /// <summary>
    /// Identity → shared reference WITHOUT materializing anything new: match the
    /// requested simple name against the live AppDomain (the missing-assembly
    /// closure of <c>MeshScriptGlobals</c> is by definition loaded — it's running
    /// this code), fall back to a file next to the referencing assembly. Returns
    /// null when neither matches — only then may the caller consult Roslyn's own
    /// (eagerly materializing) resolver.
    /// </summary>
    public static PortableExecutableReference? TryResolveByIdentity(
        AssemblyIdentity identity,
        string? referencingAssemblyPath)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            var name = asm.GetName();
            if (!string.Equals(name.Name, identity.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            var reference = TryGetOrCreate(asm);
            if (reference is not null)
                return reference;
        }

        if (!string.IsNullOrEmpty(referencingAssemblyPath))
        {
            var dir = Path.GetDirectoryName(referencingAssemblyPath);
            if (!string.IsNullOrEmpty(dir))
            {
                var sibling = Path.Combine(dir, identity.Name + ".dll");
                var reference = TryGetOrCreate(sibling);
                if (reference is not null)
                    return reference;
            }
        }

        return null;
    }
}

/// <summary>
/// Drop-in replacement for <see cref="ScriptMetadataResolver.Default"/> that funnels
/// every resolution through the process-shared materializations in
/// <see cref="KernelScriptReferences"/>. Without this, the script compilation's
/// missing-assembly resolution (the transitive closure of the globals type — the
/// whole MeshWeaver graph) re-materializes ~350 native metadata blocks per kernel
/// session; see <see cref="KernelScriptReferences"/> for the full leak analysis.
/// </summary>
internal sealed class SharedScriptMetadataResolver : MetadataReferenceResolver
{
    public static readonly SharedScriptMetadataResolver Instance = new();

    private readonly ScriptMetadataResolver inner = ScriptMetadataResolver.Default;

    private SharedScriptMetadataResolver() { }

    public override bool ResolveMissingAssemblies => true;

    public override PortableExecutableReference? ResolveMissingAssembly(
        MetadataReference definition,
        AssemblyIdentity referenceIdentity)
    {
        // 🚨 Resolve identity → SHARED reference ourselves first. Calling the inner
        // resolver here is itself the leak: RuntimeMetadataReferenceResolver's file
        // provider EAGERLY materializes the full native metadata block before we
        // could dedupe — the per-session blocks of every discarded duplicate are
        // never reclaimed. Only fall through to the inner resolver (then re-key the
        // result) for identities that genuinely aren't loaded anywhere.
        var shared = KernelScriptReferences.TryResolveByIdentity(
            referenceIdentity,
            (definition as PortableExecutableReference)?.FilePath);
        if (shared is not null)
            return shared;
        return KernelScriptReferences.Share(inner.ResolveMissingAssembly(definition, referenceIdentity));
    }

    public override ImmutableArray<PortableExecutableReference> ResolveReference(
        string reference,
        string? baseFilePath,
        MetadataReferenceProperties properties)
    {
        // Direct file path (#r "C:\…\Foo.dll") → shared materialization; everything
        // else (search-path / name-based #r) goes through the inner resolver and is
        // re-keyed onto the shared instance per path.
        if (properties == MetadataReferenceProperties.Assembly
            && (reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || reference.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            && Path.IsPathRooted(reference))
        {
            var direct = KernelScriptReferences.GetOrCreateFromFile(reference);
            if (direct is not null)
                return [direct];
        }
        var resolved = inner.ResolveReference(reference, baseFilePath, properties);
        if (resolved.IsDefaultOrEmpty) return resolved;
        return resolved.Select(r => KernelScriptReferences.Share(r)!).ToImmutableArray();
    }

    public override bool Equals(object? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => typeof(SharedScriptMetadataResolver).GetHashCode();
}
