using System.Collections.Immutable;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The FIRST rollout of a brand-new instance has nothing to protect</b> — measured on
/// <c>pearl.meshweaver.cloud</c>, 2026-09-15/16. A freshly provisioned portal served <c>503</c> at
/// the edge for nine hours with a RUNNING pod:
///
/// <code>
/// Health check nodetype_bake with status Unhealthy … 'NodeType bake regressed on this image —
///   refusing readiness so the rollout stalls with the previous image still serving.
///   2 NodeType(s) regressed on this image: GoogleMaps/Gallery, MyAi/Panel'
/// </code>
///
/// <para>Neither type ever built there: the plugin registry marks both packages
/// <c>preInstalled</c>, so their CONTENT arrives on every instance, while the modules they bind
/// (<c>MeshWeaver.Blazor.GoogleMaps</c> and friends) are store-delivered and had not landed. Every
/// entry in that instance's bake report was <see cref="BakeState.NeverBuilt"/>, and
/// <see cref="NodeTypeBakeEntry.WasHealthy"/> counts NeverBuilt as healthy — so a first-ever
/// compile failure was filed as a REGRESSION and the pod gated itself forever.</para>
///
/// <para>The gate's own sentence is the argument: it refuses readiness <i>"so the rollout stalls
/// with the previous image still serving"</i>. On a first rollout there is no previous image and
/// no previous pod. Refusing readiness protects nobody and yields an instance that can never
/// become ready — the failure mode is total, and at the edge it looks like a dead application
/// rather than a content problem.</para>
///
/// <para>So gating is suspended for that one case, and ONLY that case: a report in which every
/// type is NeverBuilt. One baked type means a working instance, where a new failure can genuinely
/// be this image's doing and still gates. Nothing is swallowed either — the outcome is recorded in
/// <see cref="NodeTypeBakeGateState.WithoutBaseline"/> and named in the health payload.</para>
///
/// <para>🚨 <b>It is carried as its own field</b> (<see cref="PreWarmOutcome.HasRegressionBaseline"/>),
/// not by emptying the baseline — MeshWeaver#4496. The first cut of this rule emptied
/// <see cref="DynamicTypePreWarmer.RegressionBaseline"/>, which made every outcome of a first bake
/// say <c>WasHealthyBeforeBake == false</c>: the right verdict reached through a statement that is
/// false about a never-built type, and one the OTHER reader of that field never agreed with —
/// <c>BuildProtocolDriver.OutcomesOf</c> stamps it straight off the entry, so the GO saw
/// <c>true</c> where the gate saw <c>false</c> for the same type. Two questions ("was this type
/// working?", "is there anything here to protect?"), two fields, and only their conjunction
/// gates.</para>
/// </summary>
public class AFirstRolloutHasNoRegressionBaselineTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static NodeTypeBakeReport Report(params (string Path, BakeState State)[] entries) =>
        new(entries.Select(e => new NodeTypeBakeEntry(e.Path, e.State)).ToImmutableList(), "framework-1");

    [Fact(Timeout = 60000)]
    public void EveryTypeNeverBuilt_IsAFirstBake_AndStaysHealthyInTheBaseline()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var report = Report(("GoogleMaps/Gallery", BakeState.NeverBuilt), ("MyAi/Panel", BakeState.NeverBuilt));

        report.Entries.Should().OnlyContain(e => e.WasHealthy,
            "the ENTRY's own meaning is unchanged — a type nobody has built is not damaged goods");

        DynamicTypePreWarmer.IsFirstBake(report).Should().BeTrue(
            "every entry is NeverBuilt — this is the fact that says the gate has nothing to protect");

        // 🚨 #4496. The baseline must NOT be emptied here. Emptying it reached the right verdict
        // through a false statement: every outcome then carried WasHealthyBeforeBake == false —
        // "this type was broken on the way in" — about types nothing had ever built. The
        // non-gating rule lives in IsFirstBake, which is carried to the gate separately.
        DynamicTypePreWarmer.RegressionBaseline(report).Should()
            .Contain("GoogleMaps/Gallery").And.Contain("MyAi/Panel",
                "the baseline reports which types were WORKING, and a never-built type is not a broken one; "
                + "whether the baseline may GATE is the separate question IsFirstBake answers");
    }

    [Fact(Timeout = 60000)]
    public void OneBakedType_MeansAnEstablishedInstance_SoTheBaselineStands()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // The negative control for the rule itself: change "every entry is NeverBuilt" to anything
        // wider and this baseline empties too, which would silently disarm the gate on every
        // established instance — the exact protection the gate exists to provide.
        var report = Report(("Crm/Contact", BakeState.Baked), ("Crm/Mail", BakeState.NeverBuilt));

        DynamicTypePreWarmer.IsFirstBake(report).Should().BeFalse("one type has been built here");
        DynamicTypePreWarmer.RegressionBaseline(report).Should().Contain("Crm/Mail",
            "a healthy entry keeps its place in the baseline whenever the instance has built anything");
    }

    [Fact(Timeout = 60000)]
    public void AnEmptyReport_IsNotAFirstBake()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // Nothing to bake is not the same claim as "this instance has never built anything", and
        // only the second one may empty a baseline. Without the Count guard, "all entries are
        // NeverBuilt" is vacuously true here and the log would announce a first bake of 0 types.
        DynamicTypePreWarmer.IsFirstBake(Report()).Should().BeFalse();
    }

    [Fact(Timeout = 60000)]
    public void AFailureWithNoBaseline_DoesNotGate_AndIsNamed()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkRunning("enumerating dynamic NodeTypes");

        var watch = gate.MarkOutcome(new PreWarmOutcome(
            "GoogleMaps/Gallery", PreWarmStatus.CompileError, "CS0246: MeshWeaver.Blazor.GoogleMaps")
        {
            // The shape a REAL first bake produces (#4496): the type is healthy — nothing ever
            // broke it — and there is simply no previous build to regress from.
            WasHealthyBeforeBake = true,
            HasRegressionBaseline = false,
        });

        watch.Should().BeFalse("there is no recovery to watch for — the type never built here");
        gate.Regressions.Should().BeEmpty("a type that never built cannot have regressed on this image");
        gate.WithoutBaseline.Keys.Should().Contain("GoogleMaps/Gallery");
        gate.Phase.Should().Be(BakePhase.Running);

        gate.MarkComplete("baked in 00:02:00 — compiled=40 alreadyBaked=0");

        gate.ReadinessGranted.Should().BeTrue("a fresh instance must be able to serve its own content");
        gate.Detail.Should().Contain("GoogleMaps/Gallery").And.Contain("no working build to regress from",
            "non-blocking must not mean invisible — this is the line that explains a Degraded new portal");
    }

    [Fact(Timeout = 60000)]
    public void OnAFirstBake_ATimeoutIsStillNotAVerdict()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // 🚨 On a first bake EVERY outcome arrives with no baseline, so "no baseline" must be the
        // LAST question asked, not the first. A timeout means the sweep never got an answer — that
        // is true whether or not anything was built here before — and filing it as "failed with no
        // working build to regress from" would report a failure that was never measured.
        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkRunning("enumerating dynamic NodeTypes");

        var watch = gate.MarkOutcome(new PreWarmOutcome(
            "Crm/Contact", PreWarmStatus.TimedOut, "SubscribeRequest timed out")
        {
            WasHealthyBeforeBake = true,
            HasRegressionBaseline = false,
        });

        watch.Should().BeFalse();
        gate.Unevaluated.Keys.Should().Contain("Crm/Contact", "the sweep got no answer about this type");
        gate.WithoutBaseline.Should().BeEmpty("a timeout is not a failure to report as one");
        gate.Regressions.Should().BeEmpty();
    }

    [Fact(Timeout = 60000)]
    public void OnAFirstBake_ARetirementIsStillNotAFailure()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkRunning("enumerating dynamic NodeTypes");

        gate.MarkOutcome(new PreWarmOutcome("Kmu/Basics", PreWarmStatus.Retired, "held for un-retyped instances")
        {
            WasHealthyBeforeBake = true,
            HasRegressionBaseline = false,
        }).Should().BeFalse();

        gate.Retired.Keys.Should().Contain("Kmu/Basics",
            "the repository that owns the type stopped carrying it — no image did that, on a fresh "
            + "instance or an old one");
        gate.WithoutBaseline.Should().BeEmpty();
    }

    [Fact(Timeout = 60000)]
    public void OnAnEstablishedInstance_ACompileErrorStillGates()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // The negative control. One baked type means a working instance: a failure there can be
        // this image's doing, and the gate keeps its full strictness.
        var report = Report(("Crm/Contact", BakeState.Baked), ("Crm/Mail", BakeState.NeverBuilt));
        report.Entries.Should().Contain(e => e.State == BakeState.Baked);

        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkRunning("enumerating dynamic NodeTypes");

        var watch = gate.MarkOutcome(new PreWarmOutcome("Crm/Contact", PreWarmStatus.CompileError, "CS0246")
        {
            WasHealthyBeforeBake = true,
        });

        watch.Should().BeTrue();
        gate.Phase.Should().Be(BakePhase.Regressed);
        gate.WithoutBaseline.Should().BeEmpty();
        gate.ReadinessGranted.Should().BeFalse("a type that used to build and no longer does still holds the pod back");
    }

    [Fact(Timeout = 60000)]
    public void AnAlreadyBrokenTypeOnAnEstablishedInstance_StillDoesNotGate()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // The other half of the conjunction, and the reason it is a conjunction: an instance that
        // HAS built things before still must not gate on a type that was already sitting at Error.
        // Splitting the fields must not cost the original leniency.
        var report = Report(("Crm/Contact", BakeState.Baked), ("Kmu/Abandoned", BakeState.PreviouslyBroken));
        DynamicTypePreWarmer.IsFirstBake(report).Should().BeFalse();
        DynamicTypePreWarmer.RegressionBaseline(report).Should().NotContain("Kmu/Abandoned",
            "PreviouslyBroken is the ONE state NodeTypeBakeEntry.WasHealthy excludes");

        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkRunning("enumerating dynamic NodeTypes");

        gate.MarkOutcome(new PreWarmOutcome("Kmu/Abandoned", PreWarmStatus.CompileError, "CS0246")
        {
            WasHealthyBeforeBake = false,
            HasRegressionBaseline = true,
        }).Should().BeFalse("it was broken on the way in — this image did not do it");

        gate.Regressions.Should().BeEmpty();
        gate.WithoutBaseline.Keys.Should().Contain("Kmu/Abandoned");
        gate.MarkComplete("baked");
        gate.ReadinessGranted.Should().BeTrue(
            "one abandoned NodeType may not block every future deploy");
    }

    [Fact(Timeout = 60000)]
    public void TheStampItself_MarksAFirstBakeHealthyAndUnprotected()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // 🚨 The PRODUCTION stamp, not a hand-built outcome. Every test above supplies the two
        // fields itself, so none of them can tell whether the sweep still writes them — both
        // default to the strict value, so a deleted assignment reverts first-bake leniency with
        // every such test still green. That is exactly how #4496 arrived, one field earlier.
        var firstBake = Report(("GoogleMaps/Gallery", BakeState.NeverBuilt), ("MyAi/Panel", BakeState.NeverBuilt));
        var stamped = DynamicTypePreWarmer.BaselineStamp(firstBake)(
            new PreWarmOutcome("GoogleMaps/Gallery", PreWarmStatus.CompileError, "CS0246"));

        stamped.WasHealthyBeforeBake.Should().BeTrue("a never-built type is not damaged goods");
        stamped.HasRegressionBaseline.Should().BeFalse("nothing has ever been built on this instance");

        // The control: one baked type and the SAME outcome is fully strict again.
        var established = Report(("GoogleMaps/Gallery", BakeState.Baked), ("MyAi/Panel", BakeState.NeverBuilt));
        var strict = DynamicTypePreWarmer.BaselineStamp(established)(
            new PreWarmOutcome("GoogleMaps/Gallery", PreWarmStatus.CompileError, "CS0246"));

        strict.WasHealthyBeforeBake.Should().BeTrue();
        strict.HasRegressionBaseline.Should().BeTrue("this instance has a previous build to regress from");

        // And a type that really was broken on the way in keeps the other fact false.
        var broken = Report(("Crm/Contact", BakeState.Baked), ("Kmu/Abandoned", BakeState.PreviouslyBroken));
        DynamicTypePreWarmer.BaselineStamp(broken)(
                new PreWarmOutcome("Kmu/Abandoned", PreWarmStatus.CompileError, "CS0246"))
            .WasHealthyBeforeBake.Should().BeFalse();
    }

    [Fact(Timeout = 60000)]
    public void TheGosOwnProjection_UsesTheSameStamp()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // The OTHER production stamper. BuildProtocolDriver.OutcomesOf mints outcomes for the same
        // gate off the same report, and it deriving the facts its own way is what diverged in
        // #4496 — so it is asserted here against the same first-bake report, by the same rule.
        var firstBake = Report(("GoogleMaps/Gallery", BakeState.NeverBuilt), ("MyAi/Panel", BakeState.NeverBuilt));

        var outcomes = BuildProtocolDriver
            .OutcomesOf(firstBake, bakedDetail: "on the share", pendingDetail: "still pending")
            .ToList();

        outcomes.Should().HaveCount(2);
        outcomes.Should().OnlyContain(o => o.WasHealthyBeforeBake,
            "a never-built type is not damaged goods, whichever projection minted the outcome");
        outcomes.Should().OnlyContain(o => !o.HasRegressionBaseline,
            "and neither projection may claim a baseline this instance does not have");

        // Control: an established report, same projection, full strictness.
        var established = Report(("Crm/Contact", BakeState.Baked), ("Kmu/Abandoned", BakeState.PreviouslyBroken));
        var strict = BuildProtocolDriver
            .OutcomesOf(established, bakedDetail: "on the share", pendingDetail: "still pending")
            .ToList();

        strict.Should().OnlyContain(o => o.HasRegressionBaseline);
        strict.Single(o => o.TypePath == "Kmu/Abandoned").WasHealthyBeforeBake.Should().BeFalse();
        strict.Single(o => o.TypePath == "Crm/Contact").WasHealthyBeforeBake.Should().BeTrue();
    }

    [Fact(Timeout = 60000)]
    public void OnAFirstBake_TheGoAndTheGateReachTheSameVerdict()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // 🚨 THE #4496 REGRESSION, pinned where core can see it. BuildProtocolDriver.IsGatingFailure
        // and NodeTypeBakeGateState.MarkOutcome are deliberately two copies of one rule — the
        // driver's own doc says they are "kept in one shape here so the GO and the gate cannot
        // disagree about what a regression is". Reading the first-bake fact off a field the driver
        // stamps from the ENTRY broke exactly that, silently: both still compiled, core's suites
        // stayed green, and the disagreement only surfaced in a dependent repo's suite.
        var firstBake = new PreWarmOutcome("GoogleMaps/Gallery", PreWarmStatus.CompileError, "CS0246")
        {
            WasHealthyBeforeBake = true,
            HasRegressionBaseline = false,
        };

        BuildProtocolDriver.IsGatingFailure(firstBake).Should().BeFalse(
            "there is no previous image to protect, so the GO may not be held either");

        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkRunning("enumerating dynamic NodeTypes");
        gate.MarkOutcome(firstBake);
        gate.MarkComplete("baked");

        gate.ReadinessGranted.Should().BeTrue("the gate must agree with the GO about the same outcome");
        gate.Regressions.Should().BeEmpty();

        // And the control: flip ONLY the baseline fact and BOTH must gate, in step.
        var established = firstBake with { HasRegressionBaseline = true };
        BuildProtocolDriver.IsGatingFailure(established).Should().BeTrue();

        var strict = new NodeTypeBakeGateState { GatesReadiness = true };
        strict.MarkRunning("enumerating dynamic NodeTypes");
        strict.MarkOutcome(established);
        strict.MarkComplete("baked");
        strict.ReadinessGranted.Should().BeFalse();
    }
}
