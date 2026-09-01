#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The three verdicts, and the fourth state that is the absence of one — never conflated.</b>
///
/// <para>This is the fold the whole combo gate (#2274) rests on, and it is deliberately pure so the
/// distinctions are pinned here rather than inferred from an integration run. The property that
/// matters most is negative and is asserted from every direction: <b>only a
/// <see cref="ComboVerdictKind.Green"/> can produce <see cref="ComboClearanceKind.Cleared"/></b>.
/// A missing verdict, a missing gate, and a <see cref="ComboVerdictKind.NotVerifiable"/> verdict all
/// land somewhere that grants nothing — which is what makes it impossible for a configuration key,
/// an unregistered service or a caught exception to manufacture a clearance.</para>
///
/// <para>The second property is symmetric and just as load-bearing: none of those three REFUSES
/// either. Refusing on absent evidence would freeze every instance in the fleet the day this
/// shipped, since producing a verdict needs docker a portal pod does not have — the fail-closed rule
/// drawn one state too wide that <see cref="ReleaseGateApplicabilityTest"/> already records the cost
/// of.</para>
/// </summary>
public class ComboClearanceTest
{
    private const string Tag = "3.0.0-ci.777";

    // ── Green: the ONLY thing that clears ──

    [Fact]
    public void AGreenVerdict_Clears_AndNamesWhatItRan()
    {
        var clearance = ComboClearance.For(Tag, Green());

        clearance.Kind.Should().Be(ComboClearanceKind.Cleared);
        clearance.IsCleared.Should().BeTrue();
        clearance.Refuses.Should().BeFalse();
        clearance.Reason.Should().Contain("GREEN").And.Contain(Tag);
        clearance.Reason.Should().Contain("linux/amd64",
            "a verdict is about ONE architecture and has to say which — the amd64 and arm64 "
            + "variants of one tag carry different bytes");
    }

    /// <summary>
    /// 🚨 A Green with caveats is not an unqualified pass. <see cref="ComboVerification.Caveats"/>
    /// documents them as mandatory-to-surface: a run over a MOVING pin verified the content it
    /// happened to fetch, and a later run can resolve differently.
    /// </summary>
    [Fact]
    public void AGreenWithCaveats_StillCarriesThem_ItIsNeverAnUnqualifiedPass()
    {
        var clearance = ComboClearance.For(Tag, Green() with
        {
            Caveats = ["'Widget' was materialised from a MOVING ref"],
        });

        clearance.Kind.Should().Be(ComboClearanceKind.Cleared);
        clearance.Reason.Should().Contain("MOVING");
    }

    // ── Red: the refusal ──

    [Fact]
    public void ARedVerdict_Refuses_AndNamesEveryFailingModule()
    {
        var clearance = ComboClearance.For(Tag, Red(
            ("Widget", "Widget/Thing: compile failed — no overload for 'AddTracking'"),
            ("Gadget", "install: the package could not be installed")));

        clearance.Kind.Should().Be(ComboClearanceKind.Refused);
        clearance.Refuses.Should().BeTrue();
        clearance.IsCleared.Should().BeFalse();
        // Breadth-complete: one broken module must never hide another.
        clearance.Reason.Should().Contain("Widget").And.Contain("AddTracking");
        clearance.Reason.Should().Contain("Gadget").And.Contain("could not be installed");
    }

    // ── NotVerifiable: NEITHER ──

    /// <summary>
    /// 🚨 The state the whole issue turns on. "We could not find out" is not "all clear" (treating
    /// it as Green reproduces the outage this gate exists to prevent) and it is not "broken"
    /// (treating it as Red bricks self-update the first time evidence is missing). It grants nothing
    /// and refuses nothing, and it says WHY.
    /// </summary>
    [Fact]
    public void ANotVerifiableVerdict_NeitherClearsNorRefuses_AndSaysWhyItCouldNotAnswer()
    {
        var clearance = ComboClearance.For(Tag, NotVerifiable(
            "the gate could not run: docker is not available on this host"));

        clearance.Kind.Should().Be(ComboClearanceKind.Unverifiable);
        clearance.IsCleared.Should().BeFalse("'we could not find out' is not a pass");
        clearance.Refuses.Should().BeFalse(
            "refusing on missing evidence would freeze every instance that has no producer");
        clearance.Reason.Should().Contain("could NOT answer");
        clearance.Reason.Should().Contain("docker is not available",
            "the caveats name every reason the question went unanswered");
    }

    [Fact]
    public void ANotVerifiableVerdict_NamesTheModulesItCouldNotEvaluate()
    {
        var clearance = ComboClearance.For(Tag, NotVerifiable("the gate was not run") with
        {
            Modules =
            [
                new ModuleVerification
                {
                    ModuleId = "Widget",
                    Outcome = ModuleVerificationOutcome.NotVerified,
                    Failures = ["materialised without an index.json root"],
                },
            ],
        });

        clearance.Kind.Should().Be(ComboClearanceKind.Unverifiable);
        clearance.Reason.Should().Contain("Widget").And.Contain("index.json");
    }

    // ── no verdict at all: NEITHER, and DISTINCT from NotVerifiable ──

    /// <summary>
    /// 🚨 "The gate ran and could not answer" and "nothing has ever run the gate" are different
    /// incidents with different fixes, and an operator has to be able to tell them apart from the
    /// recorded sentence alone. Both grant nothing; only the wording separates them.
    /// </summary>
    [Fact]
    public void NoVerdictAtAll_IsItsOwnState_NotAPassAndNotARefusal()
    {
        var clearance = ComboClearance.For(Tag, verdict: null);

        clearance.Kind.Should().Be(ComboClearanceKind.NotVerified);
        clearance.IsCleared.Should().BeFalse();
        clearance.Refuses.Should().BeFalse();
        clearance.Verdict.Should().BeNull();
        clearance.Reason.Should().Contain("no combo verification has been recorded");
        clearance.Reason.Should().Contain("mw-combo-verify",
            "an unactionable absence is how a gate stays unwired for months");
    }

    [Fact]
    public void NoVerdict_CarriesWhyThisHostHasNone()
    {
        var clearance = ComboClearance.For(
            Tag, verdict: null, absence: "no combo-gate runner is registered on this host");

        clearance.Kind.Should().Be(ComboClearanceKind.NotVerified);
        clearance.Reason.Should().Contain("no combo-gate runner is registered");
    }

    /// <summary>
    /// An unregistered gate has not ANSWERED the question, it has failed to ask it — so it lands on
    /// the same no-clearance state, naming the wiring rather than the release.
    /// </summary>
    [Fact]
    public void AnUnregisteredGate_GrantsNothing_AndNamesTheWiring()
    {
        var clearance = ComboVerificationGate.NotRegistered(Tag);

        clearance.Kind.Should().Be(ComboClearanceKind.NotVerified);
        clearance.IsCleared.Should().BeFalse();
        clearance.Refuses.Should().BeFalse();
        clearance.Reason.Should().Contain(nameof(ComboVerificationGate));
        clearance.Reason.Should().Contain("AddSelfUpdate");
    }

    /// <summary>
    /// 🚨 The exhaustiveness pin. <see cref="ComboClearance.For"/> switches on
    /// <see cref="ComboVerdictKind"/> with Green and Red as EXPLICIT arms and everything else
    /// falling to Unverifiable — never the other way round. A member appended to
    /// <see cref="ComboVerdictKind"/> tomorrow must land on the state that grants nothing, and an
    /// `is not Red` test (the tempting shape) would have cleared it instead.
    /// </summary>
    [Fact]
    public void AnUnknownVerdictKind_FallsToTheStateThatGrantsNothing_NeverToCleared()
    {
        var clearance = ComboClearance.For(Tag, Green() with { Verdict = (ComboVerdictKind)999 });

        clearance.Kind.Should().Be(ComboClearanceKind.Unverifiable);
        clearance.IsCleared.Should().BeFalse();
    }

    // ── helpers ──

    private static ComboVerification Green() => new()
    {
        CandidateTag = Tag,
        ImageRef = $"meshweaver.azurecr.io/memex-portal-ai:{Tag}",
        ImageDigest = "sha256:4a63eda",
        VerifiedPlatform = "linux/amd64",
        VerifiedAt = DateTimeOffset.UtcNow,
        Verdict = ComboVerdictKind.Green,
        Modules =
        [
            new ModuleVerification
            {
                ModuleId = "Widget",
                Outcome = ModuleVerificationOutcome.Passed,
            },
        ],
    };

    private static ComboVerification Red(params (string Module, string Failure)[] failures) =>
        Green() with
        {
            Verdict = ComboVerdictKind.Red,
            Modules = failures
                .Select(f => new ModuleVerification
                {
                    ModuleId = f.Module,
                    Outcome = ModuleVerificationOutcome.Failed,
                    Failures = [f.Failure],
                })
                .ToImmutableList(),
        };

    private static ComboVerification NotVerifiable(string caveat) => Green() with
    {
        Verdict = ComboVerdictKind.NotVerifiable,
        Modules = [],
        Caveats = [caveat],
    };
}
