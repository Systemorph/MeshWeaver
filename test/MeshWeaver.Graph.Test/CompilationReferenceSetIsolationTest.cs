using System.Runtime.CompilerServices;
using MeshWeaver.Graph.Configuration;
using Microsoft.CodeAnalysis;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The isolation contract of <see cref="CompilationReferenceSet"/> — issue #890.
///
/// <para>#890 is a process-wide poisoning: once one dynamic-NodeType compile fails inside
/// Roslyn's PE metadata writer, EVERY later <c>Emit</c> in the process fails the same way — 17
/// unrelated node paths over 6m41s in the instrumented occurrence, including a trivial
/// nested-generic compilation built fresh against the same <c>MetadataReference</c> instances.
/// The only state all of those compilations shared was
/// <c>MeshNodeCompilationService._references</c>, a <c>private static readonly</c> list that had
/// twice been classified as a "write-once constant lookup" and therefore exempt from the
/// no-static-state rule. It is not: the LIST is write-once, but a
/// <see cref="PortableExecutableReference"/> owns lazily materialized, <see cref="IDisposable"/>
/// metadata and Roslyn caches derived assembly/symbol tables against that instance.</para>
///
/// <para>These tests pin the two properties that make the set mesh-owned rather than
/// process-wide. They do NOT prove #890 is fixed — that needs a CI run — but they do prove the
/// shared state it was attributed to is gone, and they fail if anyone reintroduces a
/// process-wide memo behind the same API.</para>
/// </summary>
public class CompilationReferenceSetIsolationTest
{
    /// <summary>
    /// Two meshes must not hand Roslyn the same <see cref="MetadataReference"/> objects. Roslyn's
    /// symbol tables (<c>AssemblyMetadata.CachedSymbols</c>) hang off the reference instance, so
    /// sharing one instance is what let one mesh's compile state reach another's.
    /// </summary>
    [Fact]
    public void TwoSets_ShareNoMetadataReferenceInstance()
    {
        var a = new CompilationReferenceSet().References;
        var b = new CompilationReferenceSet().References;

        // An empty set would make the intersection assertion below vacuously true.
        Assert.NotEmpty(a);
        Assert.Equal(a.Length, b.Length);

        var shared = a.Intersect(b, ReferenceEqualityComparer.Instance).ToList();
        Assert.True(shared.Count == 0,
            "each mesh must materialize its OWN references; a shared instance carries Roslyn's "
            + "cached symbol tables across mesh boundaries, which is the process-wide state "
            + $"issue #890's canary implicated. Shared instances: {shared.Count}");
    }

    /// <summary>
    /// Nothing may root a reference set — or the <see cref="MetadataReference"/> instances inside
    /// it — beyond the mesh that built it.
    ///
    /// <para>🚨 Both weak references are load-bearing, and the second is the one that catches the
    /// realistic regression. A process-wide memo keyed by file path (the shape
    /// <c>KernelScriptReferences.Materialized</c> deliberately has) would root the
    /// <b>references</b> while leaving the <c>CompilationReferenceSet</c> wrapper perfectly
    /// collectible — so a check on the wrapper alone would pass while the very state #890 is
    /// about survived. The wrapper check catches the other shape: a registry of the sets
    /// themselves.</para>
    /// </summary>
    [Fact]
    public void NeitherTheSetNorItsReferencesAreRootedByStaticState()
    {
        var (weakSet, weakReference) = MaterializeAndForget();

        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        Assert.False(weakSet.IsAlive,
            "a CompilationReferenceSet must die with the mesh that built it — if it is still "
            + "alive after a full collect, something process-wide is holding the set itself");

        Assert.False(weakReference.IsAlive,
            "the MetadataReference instances must die with it too. This is the assertion that "
            + "fails if someone reintroduces a process-wide memo keyed by assembly path: the "
            + "wrapper would still be collectible, but the references — which own the mmap'd "
            + "metadata and Roslyn's symbol tables — would be shared across every mesh again, "
            + "which is exactly the state issue #890 came down to");

        // NoInlining so the locals cannot stay live in a register / on the frame under Release JIT.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static (WeakReference Set, WeakReference Reference) MaterializeAndForget()
        {
            var set = new CompilationReferenceSet();
            var references = set.References;   // force materialization — an unmaterialized set proves nothing
            Assert.NotEmpty(references);
            return (new WeakReference(set), new WeakReference(references[0]));
        }
    }
}
