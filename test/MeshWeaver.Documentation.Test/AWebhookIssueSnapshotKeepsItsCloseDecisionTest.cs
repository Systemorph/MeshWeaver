using System.Text.Json;
using MeshWeaver.GitSync;
using Octokit;
using Xunit;
using GitHubIssue = MeshWeaver.GitSync.GitHubIssue;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>A webhook snapshot REPLACES the node's content, so a field the mapper omits is not absent —
/// it is overwritten with the default.</b> Found in review on the change that added
/// <see cref="GitHubIssue.StateReason"/>: <c>OctokitGitHubRepoClient.ToIssue</c> populated it and
/// <c>GitHubWebhookProcessor.MapIssue</c> did not, and <c>UpsertFromWebhook</c> writes that snapshot
/// as the node content — so any <c>issues</c> / <c>issue_comment</c> event after a duplicate close
/// would have stamped <see cref="GitHubIssueStateReason.Unknown"/> over
/// <see cref="GitHubIssueStateReason.Duplicate"/>.
///
/// <para>That direction is the harm. <c>Unknown</c> does not mean "no reason"; it means <b>the reason
/// was not established</b>. So the omission would not have lost information quietly, it would have
/// had a live webhook assert something FALSE about a human's decision.</para>
///
/// <para>These are pure functions of a payload and need no mesh, so they are pinned in core — the
/// same reason <c>GreenBuildPublishSignalTest</c> lives here rather than travelling to the satellite
/// that boots a mesh.</para>
/// </summary>
public class AWebhookIssueSnapshotKeepsItsCloseDecisionTest
{
    /// <summary>A webhook `issue` object, as GitHub sends it.</summary>
    private static JsonElement Payload(string stateReasonJson, string state = "closed")
        => JsonDocument.Parse(Body(stateReasonJson, state)).RootElement;

    /// <summary>
    /// GitHub's `issue` object. 🚨 The webhook's `issue` and the REST single-issue answer are the SAME
    /// schema, which is what lets one body be driven through both mappers.
    /// </summary>
    private static string Body(string stateReasonJson, string state = "closed") =>
        $$"""
            {
              "number": 4913,
              "title": "Orleans Memory stream queue-grain dequeue fails",
              "body": "…",
              "state": "{{state}}",
              "state_reason": {{stateReasonJson}},
              "user": { "login": "systemorph-com[bot]" },
              "labels": [ { "name": "bug" } ],
              "assignees": [],
              "comments": 3,
              "html_url": "https://github.com/Systemorph/MeshWeaver/issues/4913",
              "created_at": "2026-09-19T16:21:47Z",
              "updated_at": "2026-09-19T19:20:26Z",
              "closed_at": "2026-09-19T19:06:22Z"
            }
            """;

    /// <summary>
    /// The case the review found: a ticket a human closed as a duplicate, arriving over the webhook.
    /// Before the fix this read <see cref="GitHubIssueStateReason.Unknown"/>.
    /// </summary>
    [Fact]
    public void ADuplicateCloseSurvivesTheWebhook()
    {
        var snapshot = GitHubWebhookProcessor.MapIssue(Payload("\"duplicate\""));

        Assert.Equal(GitHubIssueStateReason.Duplicate, snapshot.StateReason);
        Assert.Equal(GitHubIssueState.Closed, snapshot.State);
        Assert.Equal(4913, snapshot.Number);
    }

    /// <summary>
    /// GitHub sends <c>state_reason: null</c> for an open issue, and <c>Unknown</c> is the RIGHT
    /// answer there — an open issue carries no close decision. This is the case that must NOT be
    /// "fixed" by defaulting the field to something else.
    /// </summary>
    [Fact]
    public void AnOpenIssueCarriesNoCloseDecision()
    {
        var snapshot = GitHubWebhookProcessor.MapIssue(Payload("null", state: "open"));

        Assert.Equal(GitHubIssueStateReason.Unknown, snapshot.StateReason);
        Assert.Equal(GitHubIssueState.Open, snapshot.State);
    }

    /// <summary>
    /// 🚨 The regression test for the DEFECT CLASS, not just the field: two mappers write onto one
    /// record, and that is where a field gets added to one and forgotten in the other. ONE payload —
    /// GitHub's <c>issue</c> object, which the webhook and the REST answer share — is driven through
    /// BOTH, and they must agree.
    ///
    /// <para>The REST side goes through Octokit's own deserializer rather than its constructor, and
    /// that is not a convenience: <b>Octokit's <c>Issue</c> constructor takes
    /// <c>ItemStateReason?</c> — the three-member enum — so it cannot express <c>duplicate</c> at
    /// all</b> (`CS1503`, measured while writing this). Deserializing is the path a real REST answer
    /// takes and the only one that can carry the value, which is the same fact that makes
    /// <c>.StringValue</c> the required read.</para>
    /// </summary>
    [Theory]
    [InlineData("duplicate", GitHubIssueStateReason.Duplicate)]
    [InlineData("not_planned", GitHubIssueStateReason.NotPlanned)]
    [InlineData("completed", GitHubIssueStateReason.Completed)]
    public void TheTwoMappersAgree(string wireValue, GitHubIssueStateReason expected)
    {
        var body = Body($"\"{wireValue}\"");

        var fromWebhook = GitHubWebhookProcessor.MapIssue(
            JsonDocument.Parse(body).RootElement);
        var fromRest = OctokitGitHubRepoClient.ToIssue(
            new Octokit.Internal.SimpleJsonSerializer().Deserialize<Issue>(body));

        Assert.Equal(expected, fromWebhook.StateReason);
        Assert.Equal(fromWebhook.StateReason, fromRest.StateReason);

        // Not only the new field: the close decision is worthless if the two disagree about whether
        // the issue is closed, or about which issue it is.
        Assert.Equal(fromWebhook.State, fromRest.State);
        Assert.Equal(fromWebhook.Number, fromRest.Number);
        Assert.Equal(fromWebhook.ClosedAt, fromRest.ClosedAt);
    }
}
