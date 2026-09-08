using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Systemorph/MeshWeaver#3663 — a resolved source set of ZERO is ambiguous, and the batched bake
/// must never resolve that ambiguity by guessing.
///
/// <para><b>The incident.</b> On 2026-09-08 at 00:31:22Z one boot of memex.systemorph.com resolved
/// <b>1145</b> Code nodes in its batched discovery pass where the six neighbouring boots on that
/// portal resolved 1236, 1236, 1237, 1237, 1241 and 1241 — three of them on the <i>same image</i>.
/// The four NodeTypes of the <c>Doc</c> partition matched ZERO sources in that short pass, Roslyn
/// was handed each type's generated provider file alone, and it emitted a completely
/// genuine-looking <c>CS0246: The type or namespace name 'CessionData' could not be found</c>
/// about a type declared in that very NodeType's own <c>Source/</c>. The bake gate — correctly,
/// given its input — recorded four regressions and refused readiness for the startup probe's
/// entire 3-hour budget (1080 × 10 s), until the kubelet restarted the container and the next
/// pass came back complete. One boot in twenty-nine observed across both portals in fifteen hours.
/// </para>
///
/// <para><b>Why the existing invariant did not fire.</b>
/// <see cref="NodeTypeBatchBake.DiscoveryUnestablished"/>'s predecessor required the type to
/// DECLARE its own source queries. All four use the DEFAULT ones
/// (<c>namespace:{path}/Source scope:subtree nodeType:Code</c>) — which, as
/// <see cref="DynamicTypePreWarmer.ClassifyCompileFailure"/> had already been corrected to say in
/// #1391, is how very nearly every NodeType in a real mesh is authored. The check was therefore
/// switched off for almost the whole population, and a discovery shortfall became a verdict about
/// the image.</para>
///
/// <para>The positive witness that resolves the ambiguity is the type's OWN persisted snapshot,
/// <c>NodeTypeDefinition.CurrentSourceVersions</c>. This test pins all three of its shapes —
/// populated, explicitly empty, absent — because collapsing any two of them is what produced both
/// this incident and #1204's opposite one.</para>
/// </summary>
public class EmptySourceSetNeedsAWitnessTest
{
    /// <summary>
    /// 🚨 THE REGRESSION CASE, and the one that fails on the pre-#3663 predicate: default source
    /// queries, zero matched, and the mesh's own record saying the type has two source files. The
    /// record contradicts the pass, so the pass established nothing.
    /// </summary>
    [Fact]
    public void DefaultQueries_ZeroMatched_ButTheRecordSaysSourcesExist_IsUnestablished()
        => Assert.True(NodeTypeBatchBake.DiscoveryUnestablished(
            matchedCount: 0, declaresSources: false, knownSourceCount: 2));

    /// <summary>
    /// #1204's shape, and it must keep answering the other way: the mesh AGREES there are no
    /// sources, so zero matches is the content's real answer. Classifying it as a discovery failure
    /// would abandon every batch on a mesh holding one type whose sources were deleted.
    /// </summary>
    [Fact]
    public void ZeroMatched_CorroboratedByAnExplicitlyEmptySnapshot_IsEstablished()
    {
        Assert.False(NodeTypeBatchBake.DiscoveryUnestablished(
            matchedCount: 0, declaresSources: false, knownSourceCount: 0));
        // …and the corroboration outranks a DECLARED query set too — that is the pre-existing
        // carve-out, unchanged.
        Assert.False(NodeTypeBatchBake.DiscoveryUnestablished(
            matchedCount: 0, declaresSources: true, knownSourceCount: 0));
    }

    /// <summary>
    /// An ABSENT snapshot (never watched, never compiled) is no witness at all, so the rule falls
    /// back to exactly what it was before #3663: declared queries speak, defaults do not. Widening
    /// this to "absent counts as evidence of sources" would fail the batch on every genuinely
    /// configuration-only type on its first boot.
    /// </summary>
    [Fact]
    public void AbsentSnapshot_LeavesThePreExistingRuleUntouched()
    {
        Assert.True(NodeTypeBatchBake.DiscoveryUnestablished(
            matchedCount: 0, declaresSources: true, knownSourceCount: null));
        Assert.False(NodeTypeBatchBake.DiscoveryUnestablished(
            matchedCount: 0, declaresSources: false, knownSourceCount: null));
    }

    /// <summary>
    /// 🚨 It cannot launder a real regression. Once a single source resolved, the set IS
    /// established and the compile's verdict stands — whatever the snapshot says about how many
    /// there should have been.
    /// </summary>
    [Fact]
    public void AnyMatchedSource_IsAlwaysEstablished()
    {
        Assert.False(NodeTypeBatchBake.DiscoveryUnestablished(
            matchedCount: 1, declaresSources: false, knownSourceCount: 2));
        Assert.False(NodeTypeBatchBake.DiscoveryUnestablished(
            matchedCount: 4, declaresSources: true, knownSourceCount: 4));
        Assert.False(NodeTypeBatchBake.DiscoveryUnestablished(
            matchedCount: 2, declaresSources: true, knownSourceCount: null));
    }
}
