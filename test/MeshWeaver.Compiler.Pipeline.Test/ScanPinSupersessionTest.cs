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

        // …and the recompile lands and PUBLISHES its build, superseding it. Publishing is the
        // trigger since #4013 — a mere read no longer supersedes anything (see
        // AReadOfASupersededBuildDoesNotDoomTheBuildThatSupersededIt below).
        _service.PublishLoadContextForPath(NodeName, second);

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
        _service.PublishLoadContextForPath(NodeName, second);       // evicts + disposes the first

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
    /// 🚨 #4013 — the defect direction. A READ of a superseded generation must not doom the
    /// generation that superseded it.
    ///
    /// <para>The eviction that bounds <c>_loadContexts</c> used to ride on <i>every</i> newly
    /// created path-keyed context, taking "somebody asked for a path I had not seen" as proof that
    /// that path is the current build. A hydration scan of a package's SHIPPED assembly is not
    /// that proof — and because <c>PinForScan</c> RE-RESOLVES on a refused pin, two scans of two
    /// generations of one NodeType each destroyed the context the other had just created, until
    /// the 3-attempt cap rethrew <c>ObjectDisposedException</c>, <c>CompileResultFromAssembly</c>
    /// recorded it as a compile error and the watcher PARKED the type. That is the merge-queue
    /// dequeue in #4013: <c>PluginGateRunnerTest.SelfTypedRootWithStaleCompileStamp</c> rebuilds
    /// while the shipped stale stamp is read — exactly two generations, exactly two scans, and
    /// only under the co-tenancy of a group build did they overlap.</para>
    /// </summary>
    [Fact]
    public void AReadOfASupersededBuildDoesNotDoomTheBuildThatSupersededIt()
    {
        var shipped = EmitAssembly("v3");
        var rebuilt = EmitAssembly("v8");

        // The rebuild publishes, and its post-emit scan is in flight.
        using var publishScan = _service.PinForScan(NodeName, rebuilt, publishesTheBuild: true);

        // The other half of #4013: a hydration scan READS the shipped generation.
        using var readScan = _service.PinForScan(NodeName, shipped);
        readScan.Context.LoadNodeAssembly().Should().NotBeNull(
            "the read must still get its own generation — it is not asked to give that up");

        // The published generation must be untouched: still the cache's context for its path, and
        // still open to new scans. Before the fix the read's resolve evicted it, so this resolves
        // to a DIFFERENT instance — which is precisely what makes the next pin fail and, with two
        // scans doing it to each other, exhausts the retry cap.
        using var secondPublishScan = _service.PinForScan(NodeName, rebuilt);
        secondPublishScan.Context.Should().BeSameAs(publishScan.Context,
            "a READ of a superseded generation must not supersede the published one — reads never "
            + "re-order generations, only a publish does (#4013)");
    }

    /// <summary>
    /// 🚨 #4013 — the RETRY clause, isolated. A refused pin re-resolves, and that recovery must not
    /// supersede anybody: a recovery that destroys its peers' freshly created contexts is what
    /// turns one supersession into a ping-pong.
    ///
    /// <para>The setup makes attempt 1 resolve an EXISTING (doomed-in-place) context, so attempt 1
    /// creates nothing and evicts nothing whoever asked; the fresh context is created by the RETRY.
    /// Under the old rule that create evicted the concurrent scan's context even though the retry
    /// is a recovery, not a publication.</para>
    /// </summary>
    [Fact]
    public void ARefusedPinRetriesWithoutSupersedingTheScanAlreadyUnderWay()
    {
        var published = EmitAssembly("v8");
        var peer = EmitAssembly("v3");

        // A doomed context left under the published path: attempt 1 will resolve it (created ==
        // false, so nothing is superseded there) and its pin will be refused.
        var doomed = _service.GetOrCreateLoadContextForPath(NodeName, published);
        doomed.Dispose();
        _service.GetOrCreateLoadContextForPath(NodeName, published).Should().BeSameAs(doomed,
            "the cache still holds the disposed instance — this is the state under test");

        // A scan of ANOTHER generation is already under way.
        using var peerScan = _service.PinForScan(NodeName, peer);

        // The publishing scan is refused on attempt 1 and recovers on the retry.
        using var publishScan = _service.PinForScan(NodeName, published, publishesTheBuild: true);
        publishScan.Context.Should().NotBeSameAs(doomed);
        publishScan.Context.IsDisposed.Should().BeFalse();

        using var peerAgain = _service.PinForScan(NodeName, peer);
        peerAgain.Context.Should().BeSameAs(peerScan.Context,
            "the RETRY after a refused pin must supersede nothing — only the FIRST resolve of a "
            + "publishing scan may, or two scans re-resolving against each other never terminate "
            + "(#4013)");
    }

    /// <summary>
    /// 🚨 #4013 — the POSITIVE control, in the opposite direction. Teaching reads not to supersede
    /// must not cost the bound: a published build still evicts every older generation of its
    /// NodeType, which is the whole reason <c>EvictSupersededContexts</c> exists ("the
    /// native-memory leak that drove memex to the server-GC hard limit"). If this ever goes green
    /// by accident — because eviction was removed rather than re-triggered — the per-recompile
    /// accumulation is back.
    /// </summary>
    [Fact]
    public void APublishedBuildStillEvictsEveryOlderGenerationOfItsNodeType()
    {
        var firstGeneration = EmitAssembly("v1");
        var secondGeneration = EmitAssembly("v2");
        var published = EmitAssembly("v9");

        var older1 = _service.GetOrCreateLoadContextForPath(NodeName, firstGeneration);
        var older2 = _service.GetOrCreateLoadContextForPath(NodeName, secondGeneration);
        // Premise, and it is the FIX that makes it hold: two READS leave both generations alive.
        // Under the old evict-on-any-create rule the second read already disposed the first, so a
        // bare BeFalse() here would red on the setup and hide which half moved. Say so.
        older1.IsDisposed.Should().BeFalse(
            "a READ must not supersede — this is the setup, and if it reds the reader rule moved, "
            + "not the bound");
        older2.IsDisposed.Should().BeFalse(
            "a READ must not supersede — this is the setup, and if it reds the reader rule moved, "
            + "not the bound");

        using var publishScan = _service.PinForScan(NodeName, published, publishesTheBuild: true);

        older1.IsDisposed.Should().BeTrue(
            "a PUBLISHED build must still evict the generations it supersedes — that eviction is "
            + "the bound on _loadContexts, and without it every recompile leaves one more "
            + "collectible context (and its native metadata) alive for the life of the hub");
        older2.IsDisposed.Should().BeTrue(
            "a PUBLISHED build must still evict the generations it supersedes — that eviction is "
            + "the bound on _loadContexts, and without it every recompile leaves one more "
            + "collectible context (and its native metadata) alive for the life of the hub");
        publishScan.Context.IsDisposed.Should().BeFalse(
            "the published generation is the one that is KEPT");
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
