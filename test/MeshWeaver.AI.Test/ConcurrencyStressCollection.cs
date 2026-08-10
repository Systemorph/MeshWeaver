using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The one xUnit collection that holds every <c>MeshWeaver.AI.Test</c> class which needs the
/// machine to ITSELF, so the rest of the assembly can run under
/// <c>parallelizeTestCollections: true</c> / <c>maxParallelThreads: 4</c>
/// (<c>test/MeshWeaver.AI.Test/xunit.runner.json</c>).
///
/// <para><b>Membership rule — structural, not "whatever failed last time".</b> A class belongs
/// here when BOTH hold:</para>
/// <list type="number">
///   <item>it <b>creates concurrency of its own</b> — N operations deliberately in flight at
///         once, or a dedicated pump thread — rather than doing one thing at a time; and</item>
///   <item>its verdict is a <b>wall-clock bound on that burst</b>: the assertion that fails is a
///         deadlock / lost-write detector, not a functional comparison.</item>
/// </list>
/// <para>Both halves matter. (1) alone is just a slow test — parallelism helps those. (2) alone is
/// a generous budget on sequential work, which survives sharing a box. Together they describe a
/// test that measures the machine: give it a quarter of the runner and the budget stops measuring
/// the defect it was written for and starts measuring the scheduler.</para>
///
/// <para><b>Why these budgets are never the thing to widen.</b> Every bound in these classes is a
/// deadlock or loss detector — <c>Patch_ConcurrentUpdates_NoDeadlock</c>'s 45 s, the 15 s on eight
/// concurrent <c>GetOrderedAgentsAsync</c> chains, the 30 s settle on 288 concurrent cross-mirror
/// writes. Padding one to make a starved run pass would leave a detector that detects nothing.
/// Scheduling is the correct lever precisely because the tests are right and the contention is
/// the artefact: on a 4-vCPU runner four such classes timeshare and blow bounds that hold with
/// room to spare when each has the box.</para>
///
/// <para><b>Measured, on CI, not locally.</b> The parallel opt-in was tried once before and backed
/// out: five consecutive green local runs, then three of exactly these classes failed on shard 4.
/// <c>CrossHubPatchAtomicityTest</c>'s own diagnostic settled what kind of failure it was — zero
/// write errors, the owner holding all 288 entries, the mirror advancing monotonically with zero
/// regressions and still climbing when the bound expired. Starved, not wrong. Local measurement
/// cannot arbitrate this: <c>DOTNET_PROCESSOR_COUNT=4</c> sizes the thread pool as if there were
/// four cores but does not take the other fourteen away, so a test that spawns its own concurrency
/// still gets real parallelism on a dev box.</para>
///
/// <para><c>DisableParallelization</c> (rather than merely sharing a collection) is deliberate:
/// sharing a collection alone would only stop two stress classes from overlapping EACH OTHER,
/// while still letting three ordinary classes run alongside one of them. These tests are sized
/// against an idle box, so "alone" is the property they need.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConcurrencyStressCollection
{
    /// <summary>Collection name — referenced from <c>[Collection(ConcurrencyStressCollection.Name)]</c>.</summary>
    public const string Name = "AI concurrency stress";
}
