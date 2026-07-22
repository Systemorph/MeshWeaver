using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Graph.Configuration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Memory-leak guard for dynamically-compiled NodeType assemblies.
///
/// <para>Every NodeType is compiled into a <b>collectible</b>
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> (<c>NodeAssemblyLoadContext</c>,
/// <c>isCollectible: true</c>). The whole point of "collectible" is that once the owner
/// drops it and calls <c>Unload()</c>, the GC reclaims the JIT'd native code + metadata.
/// If ANY managed reference survives — a process-wide static dictionary holding a
/// generated <see cref="Type"/>, a top-level singleton cache that is never disposed,
/// a compiled accessor delegate over the type — the ALC is <i>pinned</i> and the
/// assembly (plus its native footprint) leaks for the process lifetime. Across a
/// CI suite of dynamic-compilation tests that accumulation is what drives the
/// late-project OOM / GC-stall flakes.</para>
///
/// <para>These tests are the deterministic, dependency-free equivalent of a
/// "dotMemory delta == 0" assertion: load a REAL emitted assembly into the cache,
/// take a <see cref="WeakReference"/> to its load context, drop every strong ref,
/// dispose the owning cache, force GC, and assert the context was collected. A
/// trivial hand-written type would not exercise the pinning paths — the assembly
/// must be genuinely emitted and loaded so a real <see cref="Type"/> exists to be
/// captured by whatever cache leaks.</para>
/// </summary>
public sealed class AssemblyLoadContextLeakTest : IDisposable
{
    private readonly string _cacheDir;
    private readonly CompilationCacheService _service;

    public AssemblyLoadContextLeakTest()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), $"alc-leak-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cacheDir);
        _service = new CompilationCacheService(
            Options.Create(new CompilationCacheOptions
            {
                CacheDirectory = _cacheDir,
                EnableCompilationCache = true,
            }),
            NullLogger<CompilationCacheService>.Instance);
    }

    public void Dispose()
    {
        try { _service.Dispose(); } catch { /* idempotent */ }
        try { Directory.Delete(_cacheDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Emit a tiny, self-contained assembly so the collectible ALC genuinely loads a
    /// real <see cref="Type"/> (the thing a leaking cache would pin). Returns raw bytes.
    /// </summary>
    private static byte[] EmitTinyAssembly(string asmName, string typeName)
    {
        var code =
            $"namespace {asmName} {{ public sealed class {typeName} {{ public int Value {{ get; set; }} }} }}";
        var compilation = CSharpCompilation.Create(
            assemblyName: asmName,
            syntaxTrees: new[] { CSharpSyntaxTree.ParseText(code) },
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            },
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        result.Success.Should().BeTrue(
            "the leak probe needs a real assembly: " +
            string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        return ms.ToArray();
    }

    /// <summary>
    /// Load an emitted assembly into the cache's collectible context, USE its type
    /// (instantiate — mirrors NodeType content usage), then return ONLY a weak ref to
    /// the context. <see cref="MethodImplOptions.NoInlining"/> + the locals dying with
    /// this frame guarantees no strong reference to the assembly/type/context survives
    /// on the caller's stack across the subsequent GC.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference LoadUseAndWeakRef(string nodeName)
    {
        var asmName = $"GenAsm_{nodeName}";
        var typeName = "Widget";
        var bytes = EmitTinyAssembly(asmName, typeName);

        var assembly = _service.LoadAssemblyFromBytes(nodeName, bytes, pdbBytes: null);
        var type = assembly.GetType($"{asmName}.{typeName}");
        type.Should().NotBeNull("the emitted type must be loadable from the collectible context");
        var instance = Activator.CreateInstance(type!);
        instance.Should().NotBeNull();

        // The context the cache created to hold this assembly (same instance — GetOrAdd).
        var context = _service.GetOrCreateLoadContext(nodeName);
        return new WeakReference(context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollect(WeakReference weak)
    {
        // Collectible ALC unload finalizes on a background pass; loop a few hard GCs.
        for (var i = 0; i < 12 && weak.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        }
    }

    /// <summary>
    /// Core property: <see cref="CompilationCacheService.Dispose"/> must release every
    /// collectible context it owns so the GC can reclaim it. If this is RED, the cache
    /// itself (or a static cache a loaded type flowed into) pins the ALC even after an
    /// explicit dispose — the dispose path is not enough. If GREEN, the cache releases
    /// correctly and any remaining leak is a <i>lifetime</i> problem (the top-level
    /// singleton is simply never disposed) rather than a pinning problem.
    /// </summary>
    [Fact]
    public void DisposingCache_CollectsLoadedAssemblyContext()
    {
        var weak = LoadUseAndWeakRef("leak_probe_node");
        weak.IsAlive.Should().BeTrue("context is held by the cache before dispose");

        _service.Dispose();
        ForceCollect(weak);

        weak.IsAlive.Should().BeFalse(
            "after CompilationCacheService.Dispose() the collectible NodeAssemblyLoadContext and its " +
            "emitted assembly MUST be GC-collected — a surviving reference is the ALC leak");
    }

    /// <summary>
    /// Per-context release: <see cref="CompilationCacheService.UnloadContext"/> (the
    /// per-node unload used on recompile / release-advance) must also let the ALC be
    /// collected, without disposing the whole cache. This is the property the per-node
    /// scoped ownership relies on — when a node hub disposes, unloading its context
    /// reclaims the assembly.
    /// </summary>
    [Fact]
    public void UnloadContext_CollectsThatContext_WithoutDisposingCache()
    {
        var weak = LoadUseAndWeakRef("unload_probe_node");
        weak.IsAlive.Should().BeTrue();

        _service.UnloadContext("unload_probe_node");
        ForceCollect(weak);

        weak.IsAlive.Should().BeFalse(
            "UnloadContext must release the collectible context so per-node disposal reclaims memory");
    }

    /// <summary>Emit a tiny assembly to a UNIQUE on-disk path — one recompile's output.</summary>
    private string EmitTinyAssemblyToDisk(string asmName, string typeName)
    {
        var bytes = EmitTinyAssembly(asmName, typeName);
        // Mirror EmitToDiskWithRetry: each release writes to its own {name}_{guid} subdir, so
        // successive loads of the same node land on DIFFERENT keys in _loadContexts.
        var dir = Path.Combine(_cacheDir, $"{asmName}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dll = Path.Combine(dir, $"{asmName}.dll");
        File.WriteAllBytes(dll, bytes);
        return dll;
    }

    /// <summary>
    /// Load an emitted assembly through the path-keyed context (the live recompile path,
    /// <c>CompileResultFromAssembly</c> → <c>GetOrCreateLoadContextForPath</c>), USE its type, and
    /// return ONLY a weak ref to the context. Locals die with this <see cref="MethodImplOptions.NoInlining"/>
    /// frame so no strong ref survives on the caller's stack.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference LoadPathAndWeakRef(string nodeName, string dllPath)
    {
        var context = _service.GetOrCreateLoadContextForPath(nodeName, dllPath);
        var assembly = context.LoadNodeAssembly();
        assembly.Should().NotBeNull("the emitted assembly must load from its path-keyed context");
        var type = assembly!.GetTypes().FirstOrDefault(t => t.IsClass);
        type.Should().NotBeNull();
        Activator.CreateInstance(type!).Should().NotBeNull();
        return new WeakReference(context);
    }

    /// <summary>
    /// 🚨 The per-recompile reclaim (the memex native-memory leak). A long-lived NodeType hub is
    /// recompiled repeatedly WITHOUT tearing down; each recompile writes a new unique path and loads
    /// it via <see cref="CompilationCacheService.GetOrCreateLoadContextForPath"/>. Loading the NEW
    /// path must evict + collect the SUPERSEDED context for the same NodeType then and there — not
    /// only on hub teardown (<c>UnloadNodeContexts</c>). If RED, every recompile pins another
    /// collectible ALC + its native metadata/JIT for the hub's whole life → unbounded growth to the
    /// GC hard limit → the GC-thrash crash. The current context must survive (not over-evicted).
    /// </summary>
    [Fact]
    public void RecompileToNewPath_EvictsAndCollects_SupersededContext_WithoutTeardown()
    {
        const string node = "recompile_evict_node";

        var v1Dll = EmitTinyAssemblyToDisk($"GenAsm_{node}_v1", "Widget");
        var weakV1 = LoadPathAndWeakRef(node, v1Dll);
        weakV1.IsAlive.Should().BeTrue("V1's context is held by the cache after the first load");

        // A recompile: a NEW unique path for the SAME node. Loading it evicts V1's superseded context.
        var v2Dll = EmitTinyAssemblyToDisk($"GenAsm_{node}_v2", "Widget");
        var weakV2 = LoadPathAndWeakRef(node, v2Dll);

        ForceCollect(weakV1);

        weakV1.IsAlive.Should().BeFalse(
            "loading a new path for the same NodeType must evict + collect the SUPERSEDED " +
            "AssemblyLoadContext without waiting for hub teardown — otherwise every recompile leaks an ALC");
        weakV2.IsAlive.Should().BeTrue(
            "the CURRENT context (just-loaded V2) must NOT be evicted — it is the live assembly the hub runs on");
    }
}
