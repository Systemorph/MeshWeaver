using Memex.Portal.Shared.Email;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the shape of <see cref="OutboundEmailSender.WatchQuery"/> against the omitted-default
/// serialization trap that stranded the Store contact form's notification email on memex
/// (2026-08-12): <see cref="EmailStatus.New"/> is the enum default, the serializer omits default
/// values from stored JSON, so a query POSITIVELY matching <c>content.status:New</c> matches
/// NOTHING that is actually new — proven live (the status-filtered query returned 0 rows while
/// the unfiltered one returned the queued email). NEGATIONS are safe AND keep the live set
/// bounded: a negation on an omitted field never excludes (also proven live —
/// <c>-content.status:New</c> still returned the status-omitted email), while explicitly stamped
/// terminal states drop out instead of accumulating forever.
/// </summary>
public class OutboundEmailWatchQueryTest
{
    [Fact]
    public void WatchQuery_MustNotPositivelyMatchStatus_TheDefaultIsOmittedFromStoredJson()
    {
        // The trap: a positive match on the omittable default. Negated clauses are fine —
        // strip them before asserting nothing positive remains.
        var withoutNegations = OutboundEmailWatchQueryTestHelpers.StripNegatedClauses(
            OutboundEmailSender.WatchQuery);
        Assert.DoesNotContain("content.status", withoutNegations);
        Assert.Contains("content.direction:Outbound", withoutNegations);
        Assert.Contains($"nodeType:{EmailNodeType.NodeType}", withoutNegations);
    }

    /// <summary>The bounded shape: every non-New status is negated away, so processed mail drops
    /// out of the live set instead of re-emitting forever, while New — stored with the status
    /// OMITTED, which no negation excludes — always matches.</summary>
    [Fact]
    public void WatchQuery_NegatesEveryTerminalStatus_SoTheLiveSetStaysBounded()
    {
        Assert.Contains("-content.status:Sending", OutboundEmailSender.WatchQuery);
        Assert.Contains("-content.status:Sent", OutboundEmailSender.WatchQuery);
        Assert.Contains("-content.status:Failed", OutboundEmailSender.WatchQuery);
    }

    /// <summary>
    /// The reasoning the query shape rests on, pinned so an enum reorder cannot silently
    /// invalidate it: <c>New</c> IS the omittable default (which is why no positive status match
    /// can ever see queued mail, and why the negations cannot exclude it), and <c>Outbound</c> is
    /// NOT the direction default (which is why the positive direction match is safe — a queued
    /// outbound email always serializes it). If either assertion fails, the watch query above has
    /// to be rethought, not just this test.
    /// </summary>
    [Fact]
    public void TheDefaults_AreWhatTheQueryShapeAssumes()
    {
        Assert.Equal(EmailStatus.New, default(EmailStatus));
        Assert.NotEqual(EmailDirection.Outbound, default(EmailDirection));
    }
}

internal static class OutboundEmailWatchQueryTestHelpers
{
    /// <summary>Removes every <c>-negated:clause</c> token so a test can assert on the POSITIVE
    /// clauses alone.</summary>
    public static string StripNegatedClauses(string query) =>
        string.Join(' ',
            query.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
                .Where(token => !token.StartsWith('-')));
}
