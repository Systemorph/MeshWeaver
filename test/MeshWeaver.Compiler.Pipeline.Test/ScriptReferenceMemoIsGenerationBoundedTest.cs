using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Kernel.Hub;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 The BOUND on <c>KernelScriptReferences.Materialized</c> (issue #4003), asserted in BOTH
/// directions.
///
/// <para><b>What was wrong.</b> The memo maps an absolute assembly path to the ONE
/// <see cref="PortableExecutableReference"/> — and thus the one memory-mapped native metadata
/// block — for that file in this process. It has no eviction and no bound, and it was allowlisted
/// in <c>NoStaticCollectionsTest</c> on the claim that it is <i>"bounded by the set of assemblies
/// on disk"</i> and <i>"can pin neither meshes nor collectible NodeType contexts"</i>. The second
/// clause is true of the OBJECT GRAPH (no <see cref="Type"/>, no <see cref="AssemblyLoadContext"/>
/// is held). The first is false for NodeType assemblies: every recompile emits into a brand-new
/// <c>{nodeName}_{ticks}_{guid}/</c> directory that is never reused
/// (<c>EmitPipeline.EmitToDiskWithRetry</c>), and <c>KernelExecutor.EnsureCellSurfaceReferences</c>
/// feeds exactly those paths in through <c>GetOrCreateFromFile</c>. So each recompile of a
/// <c>cellSurface: true</c> NodeType that any kernel session referenced pinned one more native
/// metadata mapping for the life of the PROCESS — surviving both the ALC's <c>Unload()</c> and the
/// file's deletion, and therefore outliving the eviction <c>CompilationCacheService</c> performs to
/// reclaim precisely that memory.</para>
///
/// <para><b>Why both directions are mandatory.</b> The memo exists for a measured reason
/// (#2480 / #2578 / #2769): without it a kernel session re-materializes ~350 native metadata
/// blocks — ~150-200 MiB of unreclaimable native memory PER SESSION. A "fix" that stopped sharing
/// would trade an unbounded leak for the original one and look just as green against the first
/// assertion alone. So the per-generation assertion below is paired with a de-duplication
/// assertion over the real, live assembly set.</para>
/// </summary>
public sealed class ScriptReferenceMemoIsGenerationBoundedTest : IDisposable
{
    private readonly string _root;
    private readonly AssemblyLoadContext[] _contexts;

    public ScriptReferenceMemoIsGenerationBoundedTest()
    {
        _root = Path.Combine(Path.GetTempPath(), $"script-ref-memo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _contexts = new AssemblyLoadContext[2];
    }

    public void Dispose()
    {
        foreach (var ctx in _contexts)
        {
            try { ctx?.Unload(); } catch { /* best effort */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// 🚨 THE DEFECT DIRECTION. Two generations of one cell-surface NodeType assembly — the exact
    /// shape a recompile produces — must leave the memo exactly as they found it. Reverting the
    /// enforcement in <c>TryGetOrCreate</c> reddens this on its own assertion: each generation's
    /// path acquires a permanent entry that nothing ever removes.
    /// </summary>
    [Fact]
    public void ARecompiledCellSurfaceAssemblyLeavesNoPermanentEntry()
    {
        var first = PublishGeneration(index: 0, "v1");
        var second = PublishGeneration(index: 1, "v2");

        // Premise: these really are per-GENERATION builds — distinct never-reused paths, each in
        // its own collectible context. If that ever stops being true this test is measuring
        // something else and must be rebuilt rather than trusted.
        first.Path.Should().NotBe(second.Path);
        AssemblyLoadContext.GetLoadContext(first.Assembly)!.IsCollectible.Should().BeTrue(
            "a NodeType generation is loaded into a collectible context — that is what makes its "
            + "path unrepeatable and its metadata reclaimable");
        AssemblyLoadContext.GetLoadContext(second.Assembly)!.IsCollectible.Should().BeTrue();

        // This is what KernelExecutor.EnsureCellSurfaceReferences does with entry.AssemblyPath.
        var firstReference = KernelScriptReferences.GetOrCreateFromFile(first.Path);
        var secondReference = KernelScriptReferences.GetOrCreateFromFile(second.Path);

        firstReference.Should().NotBeNull(
            "the session still needs a metadata reference for the generation it binds — the fix "
            + "changes the reference's LIFETIME, never whether it is produced");
        secondReference.Should().NotBeNull();
        secondReference.Should().NotBeSameAs(firstReference,
            "two generations are two different files and must never collapse onto one reference");

        // 🚨 Per PATH, not by a count delta: the process runs other tests concurrently, and a count
        // that another test can move measures the wrong thing. These two paths are ours alone.
        KernelScriptReferences.IsMemoized(first.Path).Should().BeFalse(
            "a per-generation NodeType assembly must leave NO permanent entry in the process-wide "
            + "memo — every recompile mints a path that is never reused, so one entry per recompile "
            + "is an unbounded mmap leak that outlives both the ALC unload and the file (#4003)");
        KernelScriptReferences.IsMemoized(second.Path).Should().BeFalse(
            "a per-generation NodeType assembly must leave NO permanent entry in the process-wide "
            + "memo — every recompile mints a path that is never reused, so one entry per recompile "
            + "is an unbounded mmap leak that outlives both the ALC unload and the file (#4003)");
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL — the one #2480 / #2578 make mandatory. The whole point of the memo
    /// is that the ~350 assemblies a script session compiles against are materialized ONCE per
    /// process and shared by every session. A fix that re-materializes per session reintroduces the
    /// ~200 MiB-per-session native leak the memo was written to remove, so this asserts the sharing
    /// directly: two independent "sessions" must get back the SAME reference instances.
    /// </summary>
    [Fact]
    public async Task TheFrameworkReferenceSetIsStillDeduplicatedAcrossSessions()
    {
        using var cts = new CancellationTokenSource(TestTimeouts.Convergence);

        var sessionOne = await KernelScriptReferences.GetReferencesAsync([], cts.Token);
        var sessionTwo = await KernelScriptReferences.GetReferencesAsync([], cts.Token);

        // 🚨 The premise is stated by CONTENT, not by a count. A count is a knife edge: how many
        // assemblies this process has loaded depends on which other tests ran first, and a
        // threshold near that number turns the control into a load-order lottery (measured: 48 in a
        // filtered run, ~350 in a portal). Naming two assemblies the set must contain says the same
        // thing — this is the live production reference set — and cannot drift.
        var paths = sessionOne.OfType<PortableExecutableReference>()
            .Select(r => r.FilePath)
            .Where(p => p is { Length: > 0 })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        paths.Contains(typeof(object).Assembly.Location).Should().BeTrue(
            "the reference set must be the live production assembly list — without the framework "
            + "itself in it this is no longer measuring the sharing it exists to measure");
        paths.Contains(typeof(MeshWeaver.Mesh.MeshNode).Assembly.Location).Should().BeTrue(
            "…and without the MeshWeaver graph in it either");

        // 🚨 Asserted in the direction that cannot race: assemblies only ever LOAD, so a second
        // session may legitimately see MORE than the first — but every reference the first session
        // used must come back as the SAME INSTANCE, or nothing is being shared.
        var sharedByIdentity = sessionOne
            .Count(first => sessionTwo.Any(second => ReferenceEquals(first, second)));
        sharedByIdentity.Should().Be(sessionOne.Length,
            "every reference a second session re-uses must be the SAME INSTANCE the first session "
            + "used — Roslyn shares the underlying AssemblyMetadata across compilations only when "
            + "the reference instance is shared, so instance identity IS the de-duplication that "
            + "keeps ~350 native metadata blocks from being re-created per kernel session "
            + "(#2480/#2578)");
    }

    /// <summary>
    /// The other half of the positive control, at the memo's own door: an ordinary assembly at a
    /// STABLE path (the framework set, the module set, NuGet's packages folder) is still memoized,
    /// so repeated resolution is a dictionary hit rather than a fresh mmap.
    /// </summary>
    [Fact]
    public void AStablePathIsStillMemoized()
    {
        var stable = typeof(KernelScriptReferences).Assembly.Location;
        stable.Should().NotBeNullOrEmpty();

        var first = KernelScriptReferences.GetOrCreateFromFile(stable);
        var second = KernelScriptReferences.GetOrCreateFromFile(stable);

        first.Should().NotBeNull();
        second.Should().BeSameAs(first,
            "a stable assembly path must resolve to the ONE shared materialization — that sharing "
            + "is the memo's entire purpose");
        KernelScriptReferences.IsMemoized(stable).Should().BeTrue(
            "a stable assembly path IS what the memo is for — the bound narrows its key space, it "
            + "must not empty it");
    }

    /// <summary>
    /// 🚨 #4003's second defect: <c>TryResolveByIdentity</c> matched on SIMPLE NAME against the
    /// whole live AppDomain, with none of the <c>IsCollectible</c> filtering its sibling carries —
    /// so a superseded generation of a recompiled NodeType, which lingers in
    /// <c>GetAssemblies()</c> until it is collected, could be handed to a script compilation as
    /// "the" assembly of that name. Which generation you got was whichever the enumeration reached
    /// first: arbitrary, unknowable at the call site, and the CS0433-between-two-generations shape.
    /// </summary>
    [Fact]
    public void ResolutionByIdentityNeverAnswersWithACollectibleGeneration()
    {
        var generation = PublishGeneration(index: 0, "v1");
        var simpleName = generation.Assembly.GetName().Name!;

        // Premise: the collectible generation IS reachable by simple name from the live AppDomain —
        // otherwise the guard below would be asserting an absence that nothing could produce.
        AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => !a.IsDynamic
                      && string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase)
                      && AssemblyLoadContext.GetLoadContext(a)?.IsCollectible == true)
            .Should().BeTrue("the loaded generation is the thing the resolver must decline to return");

        var resolved = KernelScriptReferences.TryResolveByIdentity(
            new AssemblyIdentity(simpleName), referencingAssemblyPath: null);

        resolved.Should().BeNull(
            "a collectible per-generation assembly must never be returned by a SIMPLE-NAME scan — "
            + "superseded generations linger in GetAssemblies(), so the one the enumeration reaches "
            + "first is arbitrary and possibly stale; answering null sends the caller to the "
            + "sibling probe and Roslyn's own resolver instead of answering confidently wrong");

        // …and the guard must not have shut the door on the non-collectible set the resolver exists
        // to serve.
        var framework = KernelScriptReferences.TryResolveByIdentity(
            new AssemblyIdentity(typeof(object).Assembly.GetName().Name!),
            referencingAssemblyPath: null);
        framework.Should().NotBeNull(
            "an ordinary loaded assembly must still resolve by identity — that is the path that "
            + "keeps the script compilation's missing-assembly closure off Roslyn's eagerly "
            + "materializing resolver");
    }

    private sealed record Generation(string Path, Assembly Assembly);

    /// <summary>
    /// Emits one generation of a NodeType-shaped assembly into its own never-reused directory —
    /// the layout <c>EmitToDiskWithRetry</c> produces — and loads it into its own COLLECTIBLE
    /// context, exactly as <c>CompilationCacheService</c> does.
    /// </summary>
    private Generation PublishGeneration(int index, string version)
    {
        var name = "DynamicNode_ScriptRefMemoProbe";
        var dir = Path.Combine(_root, $"{name}_{DateTime.UtcNow.Ticks:x}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dll = Path.Combine(dir, $"{name}.dll");

        var compilation = CSharpCompilation.Create(
            assemblyName: name,
            syntaxTrees: [CSharpSyntaxTree.ParseText(
                $"public sealed class ProbeContent {{ public string Version => \"{version}\"; }}")],
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var emitted = compilation.Emit(dll);
        emitted.Success.Should().BeTrue(
            string.Join("; ", emitted.Diagnostics.Select(d => d.ToString())));

        var context = new AssemblyLoadContext($"{name}-{index}", isCollectible: true);
        _contexts[index] = context;
        return new Generation(dll, context.LoadFromAssemblyPath(dll));
    }
}
