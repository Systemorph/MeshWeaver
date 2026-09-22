using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A NodeType whose PARTITION is gone must not be pumped, waited on, or turned into a verdict
/// about the image</b> — <see href="https://github.com/Systemorph/MeshWeaver/issues/5073">#5073</see>,
/// measured on <c>memex.systemorph.com</c> during helm revision 59: the new ReplicaSet's second pod
/// sat <c>2/3</c> for 48+ minutes, and because the rollout runs <c>maxUnavailable: 0</c> the old pod
/// could not be retired — two builds served one host for the whole window, which users saw as
/// dropped MCP sessions and read timeouts.
///
/// <para><b>The root cause is one missing question, and it is not the one the incident's log first
/// suggests.</b> The bake sweep's population is a live <c>nodeType:NodeType partitions:all</c> query,
/// so it enumerates rows for NodeTypes in <c>UWDeepfield</c> — a partition that, in the maintainer's
/// words, "hasn't existed in ages". Nothing then asks whether those rows still have a store behind
/// them, and the ONE absence test the sweep owns
/// (<see cref="DynamicTypePreWarmer.TypeNodeExists"/>) asks the SAME INDEX the row came from. A row
/// and the check that would disprove it therefore share a source, so a stale row confirms itself:
/// absence is not merely unproven, it is unaskable.</para>
///
/// <para><b>Which makes the damage arrive by two different doors, both of them dead ends:</b></para>
/// <list type="bullet">
///   <item><b>activation-driven</b> — the <c>SubscribeRequest</c> routes to a partition hub with no
///     store behind it and is never answered, so the entry costs a FULL per-type budget (5 minutes
///     at the default) and lands as <see cref="PreWarmStatus.TimedOut"/>. A timeout is correctly
///     non-gating, but <see cref="BakePhase.Running"/> withholds readiness for the whole sweep, so
///     the pod is out of rotation for that budget times the number of dead rows.</item>
///   <item><b>batch-driven</b> (which is what a readiness-gated pod uses) — the compile's state
///     write into that partition fails <c>OwnerUnreachable</c>, which is a
///     <see cref="PreWarmStatus.Faulted"/> IMAGE verdict, and the rescue that exists for exactly
///     this shape cannot fire because the index still names the node. The gate then refuses
///     readiness <i>forever</i>, which is the issue's title.</item>
/// </list>
///
/// <para><b>The cure is an INDEPENDENT witness, asked BEFORE the work.</b>
/// <see cref="PartitionExistenceProbe"/> asks the writable storage providers — the only party in
/// the mesh that knows the store rather than the index — and a type in a confirmed-absent partition
/// is reported <see cref="PreWarmStatus.Removed"/> without being warmed. Not a timeout added, not a
/// NotFound waved through, and nothing deleted: the row is named so an operator can clean it up.</para>
///
/// <para>🚨 <b>Fail-open is asserted here, case by case, because the wrong direction is worse than
/// the defect.</b> A false "absent" would make a pod skip real NodeTypes and serve a mesh it never
/// baked. So absence is confirmed only by a provider that SAYS SO, with none contradicting it — and
/// every other answer (indeterminate, contradicted, timed out, errored, no providers at all) leaves
/// the sweep exactly as it was. The negative control on a real monolith mesh is the last case: an
/// ordinary mesh confirms nothing absent, so this change is inert there.</para>
/// </summary>
public class ADeletedPartitionIsNotPumpedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The incident's partition, kept verbatim so the test reads against the evidence.</summary>
    private const string GonePartition = "UWDeepfield";

    private const string GoneTypePath = $"{GonePartition}/IndustryNews";

    // ——— the witness: absence is CONFIRMED by a provider, never inferred ———

    /// <summary>
    /// A writable provider that KNOWS its store and says the partition is not in it. That — and
    /// only that — is confirmed absence.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AProviderThatKnowsItsStore_ConfirmsAbsence()
    {
        var absent = await PartitionExistenceProbe
            .ConfirmedAbsent([Provider("pg", (bool?)false)], GonePartition)
            .Should().Within(TestTimeouts.Quick)
            .Emit("a provider that owns the store is the one party that can answer",
                cancellationToken: TestContext.Current.CancellationToken);

        absent.Should().BeTrue(
            "one writable provider said the partition is not in its store and nothing contradicted "
            + "it — this is the state a dropped schema leaves behind");
    }

    /// <summary>
    /// 🚨 THE FAIL-OPEN CONTROLS, one per way the question can fail to be answered. Each must read
    /// NOT-absent, so the sweep behaves exactly as it does today. A fix that skipped work on any of
    /// these would be worse than #5073: it would bake less than it reported.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData("indeterminate", "a provider with no per-partition store cannot answer")]
    [InlineData("contradicted", "another provider says the partition IS there")]
    [InlineData("errored", "a probe that threw has not answered")]
    [InlineData("silent", "a probe that never emits is bounded, and a bound is not an answer")]
    [InlineData("none", "with no writable provider nobody owns a store to miss it from")]
    public async Task NothingButAProvidersOwnFalse_ConfirmsAbsence(string shape, string because)
    {
        IReadOnlyCollection<IPartitionStorageProvider> providers = shape switch
        {
            "indeterminate" => [Provider("inmemory", (bool?)null)],
            "contradicted" => [Provider("pg", (bool?)false), Provider("blob", (bool?)true)],
            "errored" => [ThrowingProvider("pg")],
            "silent" => [SilentProvider("pg")],
            "none" => [],
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown shape"),
        };

        var absent = await PartitionExistenceProbe
            // A budget a test can wait out — the silent case must be settled BY it, and the
            // production default (5 s) is the same mechanism with a longer number.
            .ConfirmedAbsent(providers, GonePartition, probeBudget: TestTimeouts.Quick)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the probe must always answer, never fault or hang",
                cancellationToken: TestContext.Current.CancellationToken);

        absent.Should().BeFalse(because);
    }

    /// <summary>
    /// The set form answers per partition from one fan-out — the shape the sweep uses, so a dead
    /// partition costs one probe rather than one per type.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheSetForm_NamesOnlyTheConfirmedAbsentPartitions()
    {
        var providers = new[]
        {
            Provider("pg", exists: p => string.Equals(p, GonePartition, StringComparison.OrdinalIgnoreCase)
                ? false
                : true),
        };

        var absent = await PartitionExistenceProbe
            .ConfirmedAbsentAmong(providers, [GonePartition, "Admin", "rbuergi", GonePartition])
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the probe must answer for every partition asked about",
                cancellationToken: TestContext.Current.CancellationToken);

        absent.OrderBy(x => x, StringComparer.Ordinal).Should().Equal([GonePartition],
            "only the partition the provider denied is confirmed absent; duplicates collapse and "
            + "the live partitions are untouched");
    }

    /// <summary>The partition of a path is its first segment — the whole address arithmetic here.</summary>
    [Theory(Timeout = 60_000)]
    [InlineData("UWDeepfield/IndustryNews/_Thread/gather-today", "UWDeepfield")]
    [InlineData("UWDeepfield", "UWDeepfield")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void PartitionOf_IsTheFirstSegment(string? path, string expected)
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        PartitionExistenceProbe.PartitionOf(path).Should().Be(expected);
    }

    // ——— the decision: what the sweep does with the answer ———

    /// <summary>
    /// 🚨 THE REGRESSION, at the point where it is decided: a type in a confirmed-absent partition
    /// is NOT warmed, and says why. Before this existed the same input was warmed — which is the
    /// 48 minutes (activation path) or the permanent refusal (batch path).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void AConfirmedAbsentPartition_SkipsTheTypeAndNamesThePartition()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var outcome = DynamicTypePreWarmer.SkipForAbsentPartition(GoneTypePath, Absent(GonePartition));

        outcome.Should().NotBeNull(
            "warming a type whose store is gone can only burn its budget or produce a verdict no "
            + "image can fix — the sweep must decide this before it spends anything");
        outcome!.Status.Should().Be(PreWarmStatus.Removed,
            "which partitions exist is a property of the mesh, not of the framework being rolled "
            + "out, so this must land in the non-gating content bucket Removed already occupies");
        outcome.TypePath.Should().Be(GoneTypePath);
        outcome.Detail.Should().Contain(GonePartition,
            "an operator reading the report has to be told WHICH partition is gone — a skip nobody "
            + "can see is a leak nobody cleans up");
    }

    /// <summary>
    /// 🚨 THE CONTROL ON THE OTHER SIDE, and the one that keeps the case above from being vacuous:
    /// a type whose partition is NOT in the confirmed-absent set must be warmed exactly as before.
    /// A skip that fired for everything would pass the case above and black-hole every bake.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData("Admin/User", "a live partition is warmed")]
    [InlineData("UWDeepfieldExtra/Thing", "a partition whose NAME merely starts the same is warmed")]
    [InlineData("Markdown", "a type with no partition segment at all is warmed")]
    public void APartitionNobodyDenied_IsWarmedAsBefore(string typePath, string because)
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        DynamicTypePreWarmer.SkipForAbsentPartition(typePath, Absent(GonePartition))
            .Should().BeNull(because);
        DynamicTypePreWarmer.SkipForAbsentPartition(typePath, Absent())
            .Should().BeNull("and with nothing confirmed absent, nothing is ever skipped");
    }

    /// <summary>
    /// 🚨 THE sev:H PROPERTY ITSELF: the skipped type must not hold readiness. "Never becomes ready"
    /// was the whole cost of #5073, so it is asserted against the gate rather than inferred from the
    /// status's documentation.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void ASkippedType_DoesNotHoldReadiness()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkRunning("enumerating dynamic NodeTypes");

        var skipped = DynamicTypePreWarmer.SkipForAbsentPartition(GoneTypePath, Absent(GonePartition))!;
        // Healthy on the way in — the strictest case. A type that was already broken is excused by
        // the baseline anyway, so excusing this one would prove nothing.
        gate.MarkOutcome(skipped with { WasHealthyBeforeBake = true, HasRegressionBaseline = true });
        gate.MarkComplete("sweep finished");

        gate.ReadinessGranted.Should().BeTrue(
            "a partition the mesh no longer has is not a regression of this image, and holding the "
            + "pod out of rotation for it is what made #5073 a release blocker");
    }

    // ——— the negative control, against a REAL mesh ———

    /// <summary>
    /// 🚨 THE INERTNESS CONTROL. On an ordinary monolith mesh — whose in-memory provider has no
    /// per-partition store and therefore cannot answer — NOTHING is confirmed absent, so every
    /// pending type is warmed exactly as it was before this change. This is the assertion that
    /// makes the fix safe to ship: the new question changes behaviour only where it gets a real
    /// answer.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task OnAnOrdinaryMesh_NothingIsConfirmedAbsent_AndNothingIsSkipped()
    {
        var absent = await DynamicTypePreWarmer
            .AbsentPartitionsAmongPending(Mesh, [GoneTypePath, $"{TestPartition}/Widget"], null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the probe must answer on a real mesh, and must never fault the bake",
                cancellationToken: TestContext.Current.CancellationToken);

        absent.Should().BeEmpty(
            "no provider on this host owns a per-partition store, so none can witness one missing "
            + "— and an unanswerable question must leave the sweep untouched");
        DynamicTypePreWarmer.SkipForAbsentPartition(GoneTypePath, absent).Should().BeNull(
            "which means even the incident's own path is warmed here: the fix is inert without a "
            + "provider that actually knows");
    }

    // ——— fakes: IPartitionStorageProvider IS the storage extension point, not a mocked core seam ———

    private static ImmutableHashSet<string> Absent(params string[] partitions) =>
        partitions.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    private static IPartitionStorageProvider Provider(string name, bool? exists) =>
        new ProbeOnlyProvider(name, _ => Observable.Return(exists));

    private static IPartitionStorageProvider Provider(string name, Func<string, bool?> exists) =>
        new ProbeOnlyProvider(name, p => Observable.Return(exists(p)));

    private static IPartitionStorageProvider ThrowingProvider(string name) =>
        new ProbeOnlyProvider(name,
            _ => Observable.Throw<bool?>(new InvalidOperationException("the schema probe failed")));

    private static IPartitionStorageProvider SilentProvider(string name) =>
        new ProbeOnlyProvider(name, _ => Observable.Never<bool?>());

    /// <summary>
    /// A storage provider that implements ONLY the existence probe — which is all the probe under
    /// test ever touches. <see cref="Adapter"/> throws rather than returning a stand-in: if some
    /// future change starts reading through it, the test must fail loudly instead of quietly
    /// measuring a different thing.
    /// </summary>
    private sealed class ProbeOnlyProvider(string name, Func<string, IObservable<bool?>> probe)
        : IPartitionStorageProvider
    {
        /// <inheritdoc />
        public string Name => name;

        /// <summary>Writable — the probe deliberately asks only providers that own a store.</summary>
        public bool IsReadOnly => false;

        /// <inheritdoc />
        public IStorageAdapter Adapter => throw new NotSupportedException(
            "this provider exists to answer PartitionExists and nothing else — a caller reaching "
            + "for its adapter is testing something other than the existence probe");

        /// <inheritdoc />
        public IObservable<bool?> PartitionExists(string @namespace) => probe(@namespace);
    }
}
