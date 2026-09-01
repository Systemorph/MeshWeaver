using System;
using System.IO;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 The resolve-then-pin race, and why losing it is not survivable downstream (issue #1151).
///
/// <para>Every scan of a dynamically compiled NodeType assembly (the <c>GetTypes</c> +
/// <c>MeshNodeProviderAttribute</c> reflection that recovers a NodeType's
/// <c>HubConfiguration</c>) has to hold a <see cref="NodeAssemblyLoadContext"/> pin, so a
/// concurrent recompile cannot unload the collectible LoaderAllocator mid-metadata-resolution.
/// The scan sites used to do that in TWO statements — resolve the context, then pin it — and a
/// recompile landing in between disposes the very context the reader just resolved
/// (<c>GetOrCreateLoadContextForPath</c> → <c>EvictSupersededContexts</c> →
/// <c>UnloadContext</c>). The pin then throws on a reference that was live one instruction
/// earlier.</para>
///
/// <para>That is a transient supersession, not a broken assembly, and
/// <see cref="NodeAssemblyLoadContext.Pin"/> says so in its own contract: <i>"the caller must
/// then re-resolve against the current context rather than scan a doomed assembly"</i>. No
/// caller implemented it. The swallow downstream turned it into an EMPTY configuration list
/// that the per-instance activation path reads as authoritative — the hub binds the mesh
/// defaults, and because a hub resolves its configuration exactly once at activation it serves
/// "Area not found" for its whole lifetime. On CI that is a freshly installed package whose
/// root never serves its own type's areas, purely because a recompile ran next to it.</para>
/// </summary>
public sealed class ScanPinSupersessionTest : IDisposable
{
    private const string NodeName = "Shop_Front";

    private readonly string _cacheDir;
    private readonly CompilationCacheService _service;

    public ScanPinSupersessionTest()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), $"scan-pin-{Guid.NewGuid():N}");
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
    /// The production trigger, reproduced exactly: a second compile registers its own
    /// path-keyed context, which evicts and DISPOSES the one a concurrent reader already
    /// resolved. This is the state the reader is left holding — asserted here so the fix below
    /// is pinned against a race that genuinely happens, not a hypothetical one.
    /// </summary>
    [Fact]
    public void ARecompileDoomsTheContextAConcurrentReaderAlreadyResolved()
    {
        var first = EmitAssembly("v3");
        var second = EmitAssembly("v8");

        // The reader resolves — exactly what a per-instance hub activation does when it hydrates
        // configurations from the NodeType's current LatestAssemblyPath.
        var readersContext = _service.GetOrCreateLoadContextForPath(NodeName, first);
        readersContext.IsDisposed.Should().BeFalse();

        // …and the recompile lands, superseding it.
        _service.GetOrCreateLoadContextForPath(NodeName, second);

        readersContext.IsDisposed.Should().BeTrue(
            "a fresh path-keyed context for the same NodeType evicts every superseded one — "
            + "that eviction is what makes the reader's already-resolved reference doomed");
        Assert.Throws<ObjectDisposedException>(() => readersContext.Pin());
    }

    /// <summary>
    /// The contract: resolving and pinning as ONE operation survives the supersession above.
    /// <c>PinForScan</c> re-resolves against the current context — the assembly still loads and
    /// the scan still sees its types, so the hub binds its own configuration instead of the
    /// mesh defaults.
    /// </summary>
    [Fact]
    public void PinForScan_SurvivesARecompileSupersedingTheAssembly()
    {
        var first = EmitAssembly("v3");
        var second = EmitAssembly("v8");

        _service.GetOrCreateLoadContextForPath(NodeName, first);
        _service.GetOrCreateLoadContextForPath(NodeName, second);   // evicts + disposes the first

        // The reader now asks for the assembly the NodeType still points at. Resolve-then-pin
        // would hand back the doomed context above; PinForScan must not.
        using var pinned = _service.PinForScan(NodeName, first);

        pinned.Context.IsDisposed.Should().BeFalse(
            "a scan pin must never be handed a context that is already unloading");
        pinned.Context.LoadNodeAssembly().Should().NotBeNull(
            "the assembly on disk is intact — only the context that had loaded it was superseded");
    }

    /// <summary>
    /// The harder ordering, and the reason the re-resolve terminates rather than spins: a
    /// context that was disposed while STILL the dictionary's value must be replaced, not
    /// returned again. Eviction removes before it disposes, so production does not reach this
    /// state today — but the loop's termination must not depend on that.
    /// </summary>
    [Fact]
    public void PinForScan_ReplacesADisposedContextLeftInTheCache()
    {
        var dll = EmitAssembly("v3");

        var stale = _service.GetOrCreateLoadContextForPath(NodeName, dll);
        stale.Dispose();                                   // disposed, key left in place
        _service.GetOrCreateLoadContextForPath(NodeName, dll).Should().BeSameAs(stale,
            "the cache still holds the disposed instance — this is the state under test");

        using var pinned = _service.PinForScan(NodeName, dll);

        pinned.Context.Should().NotBeSameAs(stale);
        pinned.Context.IsDisposed.Should().BeFalse();
        pinned.Context.LoadNodeAssembly().Should().NotBeNull();
    }

    /// <summary>
    /// Emits a real, loadable assembly into its own release-shaped subdirectory, mirroring the
    /// one-directory-per-compile layout <c>EmitToDiskWithRetry</c> produces (which is why each
    /// recompile yields a NEW path key and evicts the previous one).
    /// </summary>
    private string EmitAssembly(string version)
    {
        var folder = Path.Combine(_cacheDir, $"{NodeName}_{version}");
        Directory.CreateDirectory(folder);
        var dllPath = Path.Combine(folder, $"{NodeName}.dll");

        var compilation = CSharpCompilation.Create(
            assemblyName: $"DynamicNode_{NodeName}_{version}",
            syntaxTrees: [CSharpSyntaxTree.ParseText(
                "public sealed class FrontContent { public int Answer() => 42; }")],
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = compilation.Emit(dllPath);
        result.Success.Should().BeTrue(
            string.Join("; ", result.Diagnostics.Select(d => d.ToString())));
        return dllPath;
    }
}
