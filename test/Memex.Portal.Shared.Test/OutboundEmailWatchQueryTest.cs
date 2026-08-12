using Memex.Portal.Shared.Email;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the shape of <see cref="OutboundEmailSender.WatchQuery"/> against the omitted-default
/// serialization trap that stranded the Store contact form's notification email on memex
/// (2026-08-12): <see cref="EmailStatus.New"/> is the enum default, the serializer omits default
/// values from stored JSON, so a query filtering <c>content.status:New</c> matches NOTHING that is
/// actually new — proven live (the status-filtered query returned 0 rows while the unfiltered one
/// returned the queued email). <see cref="InvitationEmailSender"/> documents the identical rule
/// for <c>content.status:Pending</c>; this test makes the rule enforceable rather than a comment.
/// </summary>
public class OutboundEmailWatchQueryTest
{
    [Fact]
    public void WatchQuery_MustNotFilterOnStatus_TheDefaultIsOmittedFromStoredJson()
    {
        Assert.DoesNotContain("status", OutboundEmailSender.WatchQuery);
        Assert.Contains("content.direction:Outbound", OutboundEmailSender.WatchQuery);
        Assert.Contains($"nodeType:{EmailNodeType.NodeType}", OutboundEmailSender.WatchQuery);
    }

    /// <summary>
    /// The reasoning the query shape rests on, pinned so an enum reorder cannot silently
    /// invalidate it: <c>New</c> IS the omittable default (which is why the query must not filter
    /// on status), and <c>Outbound</c> is NOT the direction default (which is why filtering on
    /// direction is safe — a queued outbound email always serializes it). If either assertion
    /// fails, the watch query above has to be rethought, not just this test.
    /// </summary>
    [Fact]
    public void TheDefaults_AreWhatTheQueryShapeAssumes()
    {
        Assert.Equal(EmailStatus.New, default(EmailStatus));
        Assert.NotEqual(EmailDirection.Outbound, default(EmailDirection));
    }
}
