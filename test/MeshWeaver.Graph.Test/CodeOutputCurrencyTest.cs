using System;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #3249 — the stale-cell indicator must fail CLOSED.
///
/// <para><b>The defect.</b> After dispatching a run, <c>CodeNodeType</c> stamps
/// <c>Last{ExecutedAt,ExecutedBy,ActivityPath,ExecutedCodeHash}</c> onto the Code node
/// fire-and-forget; both of its failure paths only log, and the dispatch acknowledgement has
/// already been posted by then. So a node can record that it RAN while recording nothing about
/// WHAT it ran. The indicator's rule was a boolean — <c>LastExecutedAt is not null &amp;&amp;
/// hash is not null/"" &amp;&amp; fingerprint(current) != hash</c> — which answers <c>false</c>
/// ("not stale", i.e. up to date) for exactly that state. That is a WRONG claim, not a missing
/// one: the cell asserts a currency nothing substantiates.</para>
///
/// <para><b>The rule these cases pin.</b>
/// <see cref="CodeOutputCurrencyExtensions.OutputCurrency"/> answers four states, not two, and only
/// <see cref="CodeOutputCurrency.Current"/> may be rendered as up to date. A run without its
/// fingerprint is <see cref="CodeOutputCurrency.Unverified"/> — deliberately neither
/// <c>Current</c> (nothing proves it) nor <see cref="CodeOutputCurrency.Stale"/> (every node last
/// executed by a build predating the fingerprint field would light up amber at once, which trains
/// readers to ignore the indicator).</para>
///
/// <para>🚨 Pure and hub-free on purpose — the rule is a pure function of node content, and
/// deriving this class from a mesh test base would boot a mesh per case for nothing. The
/// round trip through a REAL mesh (the shape a swallowed stamp actually leaves in storage) is
/// <see cref="CodeCellCurrencyThroughTheMeshTest"/>.</para>
/// </summary>
public class CodeOutputCurrencyTest
{
    private const string Source = "var x = 1;\nx + 41";

    private static string FingerprintOf(string? code, string? language = null) =>
        CodeFingerprint.Of(code, language);

    /// <summary>
    /// The verdict the PRE-FIX predicate gave — kept verbatim so these cases cannot go vacuous.
    /// A test asserting only the new answer would pass just as happily if the state it names had
    /// never been mishandled; this is the control arm that says it was.
    /// </summary>
    private static bool PreFixIsOutputStale(CodeConfiguration? code) =>
        code is { LastExecutedAt: not null, LastExecutedCodeHash: not null and not "" }
        && CodeFingerprint.Of(code.Code, code.Language) != code.LastExecutedCodeHash;

    [Fact]
    public void ACellNobodyHasRun_SaysNothing()
    {
        var cell = new CodeConfiguration { Code = Source, IsExecutable = true };

        cell.OutputCurrency().Should().Be(CodeOutputCurrency.NeverRun,
            "a cell with no run recorded has no output to be wrong about — warning here would cry "
            + "wolf on every unrun cell in the mesh");
        cell.ProvesOutputIsCurrent().Should().BeFalse(
            "'nothing has run' is not 'the output is up to date' either — there is no output");
    }

    [Fact]
    public void ANullConfiguration_IsNeverRun()
    {
        ((CodeConfiguration?)null).OutputCurrency().Should().Be(CodeOutputCurrency.NeverRun,
            "there is no cell to judge, and a missing cell must not render a warning");
    }

    [Fact]
    public void ARecordedRunOfTheCodeOnScreen_IsCurrent()
    {
        var cell = new CodeConfiguration
        {
            Code = Source,
            LastExecutedAt = DateTimeOffset.UtcNow,
            LastExecutedCodeHash = FingerprintOf(Source),
        };

        cell.OutputCurrency().Should().Be(CodeOutputCurrency.Current,
            "the run recorded the fingerprint of what it submitted and the code has not moved — "
            + "this is the one state that may be shown as up to date");
        cell.ProvesOutputIsCurrent().Should().BeTrue();
    }

    [Fact]
    public void CodeEditedSinceTheRecordedRun_IsStale()
    {
        var cell = new CodeConfiguration
        {
            Code = Source + "\n// edited",
            LastExecutedAt = DateTimeOffset.UtcNow,
            LastExecutedCodeHash = FingerprintOf(Source),
        };

        cell.OutputCurrency().Should().Be(CodeOutputCurrency.Stale,
            "the output pane is showing the result of source the reader is no longer looking at");
        cell.ProvesOutputIsCurrent().Should().BeFalse();
    }

    [Fact]
    public void LanguageSwitchedSinceTheRecordedRun_IsStale()
    {
        var cell = new CodeConfiguration
        {
            Code = Source,
            Language = "python",
            LastExecutedAt = DateTimeOffset.UtcNow,
            LastExecutedCodeHash = FingerprintOf(Source, "csharp"),
        };

        cell.OutputCurrency().Should().Be(CodeOutputCurrency.Stale,
            "the language decides WHERE the code runs, so switching it invalidates the output as "
            + "surely as editing a line");
    }

    /// <summary>
    /// 🚨 The fail-closed case, over every marker that says "a run happened" and both shapes of a
    /// missing fingerprint. Each row is a state a partial stamp can actually leave behind.
    /// </summary>
    [Theory]
    // The whole stamp landed except the hash (a build predating the field; a narrowing merge patch).
    [InlineData(true, false, false, null)]
    [InlineData(true, false, false, "")]
    // Only the activity pointer survived — the field the output pane keys on.
    [InlineData(false, true, false, null)]
    // Only the runner survived.
    [InlineData(false, false, true, null)]
    // Everything that says "it ran" is there; only the proof of WHAT ran is missing.
    [InlineData(true, true, true, null)]
    [InlineData(true, true, true, "")]
    public void ARunWhoseFingerprintWasNotRecorded_IsNeverReportedAsUpToDate(
        bool at, bool activityPath, bool by, string? hash)
    {
        var cell = new CodeConfiguration
        {
            Code = Source,
            LastExecutedAt = at ? DateTimeOffset.UtcNow : null,
            LastActivityPath = activityPath ? "rbuergi/_Activity/abc" : null,
            LastExecutedBy = by ? "rbuergi" : null,
            LastExecutedCodeHash = hash,
        };

        cell.ProvesOutputIsCurrent().Should().BeFalse(
            "the node records that it RAN but not WHAT it ran, so nothing here substantiates "
            + "'up to date' — a cell that cannot prove it is current must not claim to be (#3249)");
        cell.OutputCurrency().Should().Be(CodeOutputCurrency.Unverified,
            "the honest answer is 'I cannot tell', which is its own state: calling it Stale would "
            + "light up every node last executed before the fingerprint existed");

        // The control arm. The pre-fix predicate answered "not stale" for precisely this row —
        // which the toolbar rendered as a plain Run button on a cell showing unaccountable output.
        PreFixIsOutputStale(cell).Should().BeFalse(
            "this row IS the state the old boolean got wrong — if it ever starts reporting true "
            + "here, the cases above have stopped discriminating and need rewriting");
    }

    /// <summary>
    /// 🚨 The MIRROR hole, found by review on this PR's first push: a node carrying the fingerprint
    /// and none of the three run markers. The verdict there is fully determinable — the hash is
    /// right in front of us — so answering <see cref="CodeOutputCurrency.NeverRun"/> would silence
    /// an indicator we can actually substantiate. That is the same fail-open shape as the defect
    /// this rule exists to remove, and it is why the fingerprint is tested FIRST.
    /// </summary>
    [Theory]
    [InlineData(Source, CodeOutputCurrency.Current)]
    [InlineData(Source + "\n// edited", CodeOutputCurrency.Stale)]
    public void AFingerprintWithNoRunMarkers_IsStillDecided_NotSilenced(
        string currentCode, CodeOutputCurrency expected)
    {
        var cell = new CodeConfiguration
        {
            Code = currentCode,
            // No LastExecutedAt, no LastActivityPath, no LastExecutedBy — only the proof itself.
            LastExecutedCodeHash = FingerprintOf(Source),
        };

        cell.OutputCurrency().Should().Be(expected,
            "the fingerprint is both evidence that a run happened and the only field that can "
            + "decide currency, so a node carrying it alone is decidable — reading it as NeverRun "
            + "would silence a verdict we can substantiate");
    }

    /// <summary>
    /// The other half of fail-closed: the rule must not be closed so hard that it never says
    /// <see cref="CodeOutputCurrency.Current"/>. An indicator that always warns is an indicator
    /// nobody reads, which is the same failure as one that never warns.
    /// </summary>
    [Fact]
    public void TheRuleStillReachesCurrent_SoTheWarningKeepsItsMeaning()
    {
        var ran = new CodeConfiguration
        {
            Code = Source,
            Language = "csharp",
            LastExecutedAt = DateTimeOffset.UtcNow,
            LastExecutedBy = "rbuergi",
            LastActivityPath = "rbuergi/_Activity/abc",
            LastExecutedCodeHash = FingerprintOf(Source, "csharp"),
        };

        ran.OutputCurrency().Should().Be(CodeOutputCurrency.Current);
    }
}
