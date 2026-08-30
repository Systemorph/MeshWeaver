using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Kernel.Hub;
using Xunit;

namespace MeshWeaver.Kernel.Test;

/// <summary>
/// 🅿️ The script reference set must reflect the assemblies loaded <b>now</b>, never the ones that
/// happened to be loaded when this process built its FIRST reference set (#2616).
///
/// <para>The defect: <c>KernelScriptReferences</c> held its reference set in a process-wide
/// <c>Lazy&lt;Task&lt;ImmutableArray&lt;…&gt;&gt;&gt;</c> built from
/// <see cref="AppDomain.CurrentDomain"/>'s assembly list. Assemblies load LAZILY, so that froze a
/// load-order lottery: whatever had not loaded by the process's first kernel session was missing
/// from every script compilation for the life of the process. A completion list silently shorts a
/// symbol; a script referencing one fails to compile. Under parallel shard load the lottery is
/// decided by whichever unrelated test ran first, which is exactly why it presented as a flake
/// reproducing on completely unrelated diffs
/// (<c>ScriptCompletions_FilterByTypedPrefix_NotJustTheAlphabet</c> losing <c>Mesh</c>).</para>
///
/// <para>🚨 This test is DETERMINISTIC by construction, not a re-run of the flake: it loads an
/// assembly <b>after</b> the first call and asserts the second call can see it. Against the frozen
/// implementation that is impossible, so it fails every time rather than occasionally.</para>
/// </summary>
public class KernelScriptReferencesFreshnessTest
{
    /// <summary>
    /// Mirrors <c>MeshScriptEnvironment.IsTestScaffolding</c>, which is what
    /// <c>KernelScriptReferences</c> filters the script surface by. Kept as a local copy rather
    /// than made public: widening a production surface so a test can read it is how the surface
    /// grows, and this predicate is three string comparisons.
    /// </summary>
    private static bool IsTestScaffolding(string assemblyName)
        => assemblyName.Equals("MeshWeaver.Fixture", StringComparison.Ordinal)
           || assemblyName.EndsWith(".TestBase", StringComparison.Ordinal)
           || assemblyName.EndsWith(".Test", StringComparison.Ordinal)
           || assemblyName.EndsWith(".Tests", StringComparison.Ordinal);

    [Fact(Timeout = 120_000)]
    public async Task AnAssemblyLoadedAfterTheFirstCall_IsStillInTheReferenceSet()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1. Build the reference set once — this is what used to freeze the assembly list.
        var before = await KernelScriptReferences.GetReferencesAsync([], ct);
        Assert.True(before.Length > 0, "the reference set is the script compilation surface");

        // 2. Find a real assembly beside this test binary that is NOT loaded yet, and load it.
        //    🚨 Asserted, not skipped: "no candidate found" would let this test pass having
        //    verified nothing, which is the one outcome that must never read as green.
        var loadedPaths = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => a.Location)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        var probeDirectory = Path.GetDirectoryName(typeof(KernelScriptReferencesFreshnessTest).Assembly.Location)!;
        var candidate = Directory.EnumerateFiles(probeDirectory, "*.dll")
            .Where(f => !loadedPaths.Contains(f))
            .Select(f =>
            {
                try { return (Path: f, Name: AssemblyName.GetAssemblyName(f)); }
                catch { return (Path: f, Name: (AssemblyName?)null); }   // native/unmanaged: not a candidate
            })
            // 🚨 TEST SCAFFOLDING IS NOT A VALID CANDIDATE. MeshScriptEnvironment.IsTestScaffolding
            // excludes MeshWeaver.Fixture and anything ending .Test/.Tests/.TestBase from the script
            // surface BY DESIGN (the 2026-08-12 "exports dead in prod" fix). Loading one of those
            // and then asserting it reaches the reference set asserts the OPPOSITE of the contract:
            // the test would fail for the wrong reason, and which .dll the directory walk happens to
            // reach first is exactly the order-sensitivity this test exists to eliminate.
            .Where(x => x.Name?.Name is { } n && !IsTestScaffolding(n))
            .FirstOrDefault(x => x.Name is not null);

        Assert.True(candidate.Name is not null,
            "this test needs one not-yet-loaded, NON-scaffolding managed assembly next to the test "
            + "binary to prove freshness; with none it would assert nothing and pass vacuously");

        var late = Assembly.LoadFrom(candidate.Path);

        // 3. The set built AFTER that load must contain it. The frozen implementation cannot,
        //    however long you wait — the list was captured in step 1.
        var after = await KernelScriptReferences.GetReferencesAsync([], ct);

        var paths = after
            .Select(r => (r as Microsoft.CodeAnalysis.PortableExecutableReference)?.FilePath)
            .Where(path => path is not null)
            .ToImmutableArray();

        Assert.True(
            paths.Any(path => string.Equals(path, late.Location, StringComparison.OrdinalIgnoreCase)),
            $"an assembly loaded after the first call must still reach the script surface, but "
            + $"'{late.Location}' was absent from {paths.Length} references — freezing the set made "
            + "visibility a load-order lottery (#2616)");
    }
}
