using System;
using MeshWeaver.Data.Serialization;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// How a view classifies the end of a followed read (MeshWeaver#7825–#7830). A read on which no
/// verdict was reached is NO VERDICT — the "temporarily unavailable" state, never "Access denied";
/// a decided refusal is an expected user-side outcome — the denied state, logged at Warning, never an
/// engineering Error. Pre-fix both reached the view as an <see cref="InvalidOperationException"/>
/// carrying the denial's text, which <see cref="AreaErrorClassifier.IsExpectedUserActionFailure"/>
/// did not recognise (Error on every view) and <see cref="AreaErrorClassifier.TryGetAccessDeniedPath"/>
/// read as a denial even when nothing had been decided.
/// </summary>
public class FollowedReadEndClassificationTest
{
    [Fact]
    public void AReadEndedWithoutAVerdict_IsAnAvailabilityFailure_NeverADenial()
    {
        var end = ReadVerdict.NoVerdict("user 'u' may keep reading 'Space/Doc'", new TimeoutException("stalled"));

        Assert.True(AreaErrorClassifier.IsAvailabilityFailure(end));
        Assert.False(AreaErrorClassifier.IsAccessDenied(end));
        Assert.Null(AreaErrorClassifier.TryGetAccessDeniedPath(end));
        Assert.False(AreaErrorClassifier.IsExpectedUserActionFailure(end), "no verdict is an engineering signal, not a user action");
    }

    [Fact]
    public void ARefusedRead_IsAnExpectedUserActionFailure()
    {
        var end = new UnauthorizedAccessException(
            "Access denied: user 'u' lacks Read permission on 'Space/Doc' — the read stream s served has ended (revoked, expired, or no longer admitted)");

        Assert.True(AreaErrorClassifier.IsExpectedUserActionFailure(end));
        Assert.True(AreaErrorClassifier.IsAccessDenied(end));
        Assert.Equal("Space/Doc", AreaErrorClassifier.TryGetAccessDeniedPath(end));
    }

    [Fact]
    public void NegativeControl_TheUntypedEndThePreFixSubscriberRaised_IsLoggedAsAnError()
    {
        // What a StreamErrorEvent re-raised before it carried its kind: the denial's text in an
        // InvalidOperationException. The log-level predicate cannot see it — every view logged Error.
        var end = new InvalidOperationException(
            "Access denied: user 'u' lacks Read permission on 'Space/Doc' — the read stream s served has ended (revoked, expired, or no longer admitted)");

        Assert.False(AreaErrorClassifier.IsExpectedUserActionFailure(end));
    }
}
