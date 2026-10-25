using MeshWeaver.GitSync;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A close is a DECISION, and the state alone does not carry it</b> —
/// Systemorph/MeshWeaver.Plugins#2177.
///
/// <para>An automated recurrence that reads only <see cref="GitHubIssueState.Closed"/> and
/// <see cref="GitHubIssue.ClosedAt"/> cannot tell "fixed" from "this is tracked on another issue".
/// For a fault that is still firing the occurrence genuinely postdates the close, so the reopen
/// proceeds and a human's deliberate consolidation is undone — measured at 11 and 14 minutes on
/// Systemorph/MeshWeaver#4913 and #4918. The missing input is GitHub's <c>state_reason</c>, and this
/// file pins the one part of reading it that is easy to get wrong.</para>
///
/// <para>🚨 <b>The trap this exists to keep closed.</b> Octokit (which the REST client used to be
/// built on) modelled the field as a three-member enum — <c>Completed</c>, <c>NotPlanned</c>,
/// <c>Reopened</c> — whose <c>.Value</c> threw on anything else. GitHub serves a fourth value,
/// <c>duplicate</c>, which is the ONE value #2177 turns on, so the mapping reads the raw wire
/// token through a total parse; the REST client's wire model keeps the field a string.</para>
/// </summary>
public class ACloseCarriesItsReasonTest
{
    /// <summary>
    /// The value the vendor enum cannot hold is the value the caller needs. Without the parse this
    /// asserts, a duplicate-closed issue is either a throw or — worse — silently
    /// <see cref="GitHubIssueStateReason.Unknown"/>, which reads as "no decision was recorded".
    /// </summary>
    [Fact]
    public void DuplicateIsParsed_TheOneReasonOctokitsOwnEnumHasNoMemberFor()
    {
        Assert.Equal(GitHubIssueStateReason.Duplicate, GitHubIssueStateReasons.Parse("duplicate"));
        Assert.Equal(GitHubIssueStateReason.Duplicate, GitHubIssueStateReasons.Parse("Duplicate"));
    }

    /// <summary>
    /// Every wire value GitHub documents, in both spellings the field travels in.
    /// </summary>
    [Theory]
    [InlineData("completed", GitHubIssueStateReason.Completed)]
    [InlineData("COMPLETED", GitHubIssueStateReason.Completed)]
    [InlineData("not_planned", GitHubIssueStateReason.NotPlanned)]
    [InlineData("notplanned", GitHubIssueStateReason.NotPlanned)]
    [InlineData("reopened", GitHubIssueStateReason.Reopened)]
    public void TheDocumentedWireValuesRoundTrip(string wireValue, GitHubIssueStateReason expected)
        => Assert.Equal(expected, GitHubIssueStateReasons.Parse(wireValue));

    /// <summary>
    /// 🚨 An absent or unrecognised reason is <see cref="GitHubIssueStateReason.Unknown"/> — an
    /// ABSENCE of evidence, never <see cref="GitHubIssueStateReason.Completed"/>. GitHub omits the
    /// field for an open issue and for anything closed before it existed, so defaulting it to
    /// "completed" would make a caller act on a decision nobody made; and the parse must not throw
    /// on a value a future GitHub invents, because that would fault the whole issue read.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("some_reason_github_has_not_invented_yet")]
    public void AnAbsentOrUnknownReasonIsUnknown_NotCompleted(string? wireValue)
        => Assert.Equal(GitHubIssueStateReason.Unknown, GitHubIssueStateReasons.Parse(wireValue));

    /// <summary>
    /// The field's default on the model, which is what a list read (and every existing caller)
    /// leaves it at.
    /// </summary>
    [Fact]
    public void AnIssueNobodyAskedAboutCarriesNoReason()
    {
        var unasked = new GitHubIssue { Number = 1, State = GitHubIssueState.Closed };

        Assert.Equal(GitHubIssueStateReason.Unknown, unasked.StateReason);
        Assert.NotEqual(unasked, unasked with { StateReason = GitHubIssueStateReason.Duplicate });
    }
}
