using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The verdict table of the execute-time interlock (Systemorph/MeshWeaver#2820) — the pure half,
/// with no hub, no mesh and no timing, so all five rows are asserted in one place and the two
/// enforcement sites cannot drift about what "refused" means.
///
/// <para>🚨 <b>The row that matters most is the one that PERMITS.</b> A gate that refuses too much
/// takes a portal down: every bundle published before producers recorded a source fingerprint
/// adopts as <see cref="BuildProvenance.AdoptedUnverified"/>, and on a
/// <c>Modules:RequirePrebuilt</c> mesh a local compile is refused by design — so folding "unknown"
/// into "refused" would park every legacy type with no recovery path at all. That is the outage
/// refusing unproven bundles was rejected to avoid, and
/// <see cref="AdoptedUnverified_IsPermitted_TheAntiOutageProperty"/> is what stops a future
/// tightening from re-introducing it silently.</para>
/// </summary>
public class NodeTypeExecutionGateTest
{
    private static NodeTypeDefinition With(BuildProvenance provenance) =>
        new()
        {
            CompilationStatus = CompilationStatus.Ok,
            LatestAssemblyCollection = "assemblies",
            LatestAssemblyPath = "Acme/Widget/v3.dll",
            AdoptedSourceFingerprint = "aaaaaaaaaaaaaaaa",
            CurrentSourceFingerprint = "bbbbbbbbbbbbbbbb",
            BuildProvenance = provenance,
        };

    /// <summary>
    /// The one hard refusal, and the case the whole mechanism exists for: the bundle NAMED the
    /// sources it was built from and they are not this mesh's, so the bytes are last week's code
    /// over today's data (#2813 — four client documents' bodies lost, one unrecoverable).
    /// </summary>
    [Fact]
    public void AdoptionRefused_RefusesExecution()
    {
        NodeTypeExecutionGate.Evaluate(With(BuildProvenance.AdoptionRefused))
            .Should().Be(BuildExecutionVerdict.Refused,
                "AdoptionRefused is the PROVEN-stale state — the only one the gate exists to stop");
        NodeTypeExecutionGate.RefusesExecution(With(BuildProvenance.AdoptionRefused))
            .Should().BeTrue();
    }

    /// <summary>
    /// 🚨 THE ANTI-OUTAGE PROPERTY. A legacy bundle carries no fingerprint, so nothing was
    /// compared: its provenance is UNKNOWN, not PROVEN STALE, and the two deserve different
    /// answers. Refusing here would park every type adopted from every bundle published to date.
    /// </summary>
    [Fact]
    public void AdoptedUnverified_IsPermitted_TheAntiOutageProperty()
    {
        NodeTypeExecutionGate.Evaluate(With(BuildProvenance.AdoptedUnverified))
            .Should().Be(BuildExecutionVerdict.Permitted,
                "unknown provenance is not proven-stale provenance — refusing it parks every "
                + "legacy-bundle type, and on a Modules:RequirePrebuilt mesh there is no local "
                + "compile to recover with");
        NodeTypeExecutionGate.RefusesExecution(With(BuildProvenance.AdoptedUnverified))
            .Should().BeFalse();
    }

    /// <summary>The bundle's fingerprint MATCHED the live source set — the bytes and the source
    /// have been compared and they agree.</summary>
    [Fact]
    public void AdoptedVerified_IsPermitted()
        => NodeTypeExecutionGate.Evaluate(With(BuildProvenance.AdoptedVerified))
            .Should().Be(BuildExecutionVerdict.Permitted);

    /// <summary>
    /// Roslyn built these bytes here, from the source this mesh holds. Also the state a refused
    /// type returns to: <c>ApplyCompileSuccess</c> resets the field, which is what stops the gate
    /// refusing a type whose live source it had just compiled itself.
    /// </summary>
    [Fact]
    public void Compiled_IsPermitted()
        => NodeTypeExecutionGate.Evaluate(With(BuildProvenance.Compiled))
            .Should().Be(BuildExecutionVerdict.Permitted);

    /// <summary>
    /// A record written before <c>BuildProvenance</c> existed reads as the zero value. It must
    /// execute — anything else would refuse every historical node on first read after a deploy.
    /// </summary>
    [Fact]
    public void ADefinitionFromBeforeTheFieldExisted_IsPermitted()
        => NodeTypeExecutionGate.Evaluate(new NodeTypeDefinition())
            .Should().Be(BuildExecutionVerdict.Permitted,
                "BuildProvenance.Compiled is deliberately the zero value");

    /// <summary>
    /// 🚨 THE THIRD STATE. A provenance read that produced no definition reached NO VERDICT — it
    /// is neither verified nor refused, and collapsing it into either is the #2901 / #2274 trap.
    /// Asserting it is not <see cref="BuildExecutionVerdict.Permitted"/> AND not
    /// <see cref="BuildExecutionVerdict.Refused"/> is the whole point: a boolean gate would have to
    /// pick one, and both picks are wrong (a silent fail-open, or a type parked on a read that
    /// merely timed out).
    /// </summary>
    [Fact]
    public void ADefinitionThatCouldNotBeRead_IsInconclusive_NeverCollapsed()
    {
        var verdict = NodeTypeExecutionGate.Evaluate(null);

        verdict.Should().Be(BuildExecutionVerdict.Inconclusive);
        verdict.Should().NotBe(BuildExecutionVerdict.Permitted,
            "a read that reached no verdict must not be read as a clean bill of health");
        verdict.Should().NotBe(BuildExecutionVerdict.Refused,
            "a probe must not answer its scariest branch on its own inability to run (#890)");
        NodeTypeExecutionGate.RefusesExecution(null).Should().BeFalse(
            "RefusesExecution is the HARD refusal only — Inconclusive is a distinct answer the "
            + "call sites handle by binding no assembly at all, not by refusing");
    }

    /// <summary>
    /// The operator-facing sentence has to carry BOTH fingerprints: the pair is what lets a human
    /// check the verdict against the bundle by hand instead of taking the platform's word for it.
    /// And it has to carry the recovery verb, because a refusal nobody can act on becomes "the
    /// portal is broken" (#2818: an hour lost to a signal that read as slowness).
    /// </summary>
    [Fact]
    public void TheRefusalSummary_NamesBothFingerprints_AndTheRecoveryVerbNamesTheForcedCompile()
    {
        var summary = NodeTypeExecutionGate.RefusalSummary(
            "Acme/Widget", With(BuildProvenance.AdoptionRefused));

        summary.Should().Contain("Acme/Widget");
        summary.Should().Contain("aaaaaaaaaaaa", "the bundle's recorded fingerprint");
        summary.Should().Contain("bbbbbbbbbbbb", "the live source fingerprint it disagreed with");

        NodeTypeExecutionGate.RecoveryVerb.Should().Contain("compile",
            "the operator must be told the verb that replaces the bytes");
        NodeTypeExecutionGate.RecoveryVerb.Should().Contain("RequirePrebuilt",
            "on a mesh that cannot compile locally the verb is different — a rebake — and that is "
            + "exactly the mesh where this refusal is reachable at all");
    }

    /// <summary>
    /// The refusal page's prose is user-visible, so it is catalog copy in BOTH shipped languages —
    /// not an English literal, and not a key that renders as a dotted token because it was never
    /// added. <c>LocalizationTest</c> cannot see a string that never became a key; this can.
    /// </summary>
    [Theory]
    [InlineData(NodeTypeEnrichmentHelpers.ExecutionRefusedSummaryKey)]
    [InlineData(NodeTypeEnrichmentHelpers.ExecutionRefusedIntroKey)]
    [InlineData(NodeTypeEnrichmentHelpers.ExecutionRefusedGuidanceKey)]
    [InlineData(NodeTypeEnrichmentHelpers.NoCodeChangeNeededKey)]
    public void TheRefusalCopy_ResolvesInEveryShippedLanguage(string key)
    {
        foreach (var locale in new[] { "en", "de" })
        {
            var text = LocalizationCatalog.Get(key, locale);
            text.Should().NotBeNullOrWhiteSpace();
            text.Should().NotBe(key,
                $"'{key}' must be present in strings.{locale}.json — the catalog echoes the key "
                + "back when it is missing, which renders a dotted token on the refusal page");
        }

        LocalizationCatalog.Get(key, "de").Should().NotBe(
            LocalizationCatalog.Get(key, "en"),
            "a German viewer must not be shown the English sentence");
    }

    /// <summary>
    /// 🚨 The page's headline is CATALOG COPY with the three facts as arguments — not
    /// <see cref="NodeTypeExecutionGate.RefusalSummary"/>, whose English belongs to the log line and
    /// the delivery NACK. This is the split that keeps a German viewer from reading an English
    /// sentence while the operator-facing surfaces stay greppable in one language.
    /// </summary>
    [Fact]
    public void ThePageHeadline_IsCatalogCopy_WithTheThreeFactsSubstituted()
    {
        var def = With(BuildProvenance.AdoptionRefused);
        var (adopted, live) = NodeTypeExecutionGate.Fingerprints(def);

        foreach (var locale in new[] { "en", "de" })
        {
            var rendered = LocalizationCatalog.Get(
                NodeTypeEnrichmentHelpers.ExecutionRefusedSummaryKey, locale,
                "Acme/Widget", adopted, live);

            rendered.Should().Contain("Acme/Widget", "the node path is a wire identifier — verbatim");
            rendered.Should().Contain(adopted, "the bundle's fingerprint is opaque — verbatim");
            rendered.Should().Contain(live, "and so is the live one");
            rendered.Should().NotContain("{0}", "every placeholder must be filled");
            rendered.Should().NotContain("PROVEN stale",
                "the page must not fall back to the English log sentence");
        }
    }

    /// <summary>
    /// 🚨 THE WIRING, not just the catalog. With an ECHO localizer every user-visible string this
    /// page supplies comes back as its own dotted KEY — so an English literal, or a regression that
    /// pipes <see cref="NodeTypeExecutionGate.RefusalSummary"/> straight onto the page again,
    /// stands out as prose among keys. Asserting the catalog alone would not catch either: the keys
    /// can be perfectly translated while nothing reads them.
    /// </summary>
    [Fact]
    public void EveryStringTheRefusalPageSupplies_ComesFromTheCatalog()
    {
        var markdown = NodeTypeEnrichmentHelpers.BuildExecutionRefusedMarkdown(
            localize: (key, _) => key,
            nodeType: "Acme/Widget",
            fingerprints: ("aaaaaaaaaaaa", "bbbbbbbbbbbb"),
            activityPath: "Acme/Widget/_Activity/compile");

        foreach (var key in new[]
                 {
                     NodeTypeEnrichmentHelpers.ExecutionRefusedSummaryKey,
                     NodeTypeEnrichmentHelpers.ExecutionRefusedIntroKey,
                     NodeTypeEnrichmentHelpers.ExecutionRefusedGuidanceKey,
                     NodeTypeEnrichmentHelpers.NoCodeChangeNeededKey,
                     "ui.viewCompileLog",
                 })
            markdown.Should().Contain(key,
                $"'{key}' must be read through the localizer, not baked in English");

        markdown.Should().NotContain("PROVEN stale",
            "the log/NACK sentence must never reach the page — that is the hard-coded-UI-string bug");
        markdown.Should().NotContain("Recompile this type",
            "and neither must the operator-facing recovery verb, which is English by design");
    }

    /// <summary>The fingerprints are shortened identically wherever they are reported, so the page,
    /// the log line and the NACK all name the same twelve characters.</summary>
    [Fact]
    public void Fingerprints_AreShortenedTheSameWayEverywhere()
    {
        var (adopted, live) = NodeTypeExecutionGate.Fingerprints(With(BuildProvenance.AdoptionRefused));

        adopted.Should().Be("aaaaaaaaaaaa");
        live.Should().Be("bbbbbbbbbbbb");
        NodeTypeExecutionGate.RefusalSummary("Acme/Widget", With(BuildProvenance.AdoptionRefused))
            .Should().Contain(adopted).And.Contain(live);

        NodeTypeExecutionGate.Fingerprints(new NodeTypeDefinition())
            .Should().Be(("(none)", "(none)"),
                "an absent fingerprint reads as absent, never as an empty string a reader would "
                + "mistake for a match");
    }
}
