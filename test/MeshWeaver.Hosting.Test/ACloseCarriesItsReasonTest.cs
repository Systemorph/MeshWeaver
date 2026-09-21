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
/// <para>🚨 <b>The trap this exists to keep closed.</b> Octokit models the field as
/// <c>StringEnum&lt;ItemStateReason&gt;?</c> and its <c>ItemStateReason</c> has exactly three
/// members — <c>Completed</c>, <c>NotPlanned</c>, <c>Reopened</c>. GitHub serves a fourth value,
/// <c>duplicate</c>, which is the ONE value #2177 turns on. Reading it through <c>.Value</c> throws
/// rather than answering, so the mapping goes through the raw token and a total parse.</para>
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
    /// 🚨 The negative control on the design choice, run against the vendor type itself so the
    /// rationale in <see cref="GitHubIssueStateReason"/> cannot quietly become false: if a later
    /// Octokit adds <c>Duplicate</c> this fails, and the comment saying "the vendor enum cannot hold
    /// it" is the thing that needs updating.
    /// </summary>
    [Fact]
    public void OctokitsOwnEnumStillCannotHoldDuplicate_WhichIsWhyTheRawTokenIsRead()
    {
        var asOctokitModelsIt = new Octokit.StringEnum<Octokit.ItemStateReason>("duplicate");

        Assert.Equal("duplicate", asOctokitModelsIt.StringValue);
        Assert.Throws<ArgumentException>(() => asOctokitModelsIt.Value);
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
