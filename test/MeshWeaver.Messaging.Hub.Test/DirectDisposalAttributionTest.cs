using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 A teardown that does not come over the bus can still say who did it and why (#4888).
///
/// <para>Until this, every direct <c>Dispose()</c> reported itself as
/// <c>"a direct Dispose() (no routed DisposeRequest)"</c> — honest, and useless to the reader of a
/// <c>[DISPOSE-DISCARD]</c>, which is the Error that becomes an ISSUE. That report names the
/// discarded message, its sender and the gates it sat behind, and then could not say which teardown
/// threw it away. Measured on <c>Admin/_LogIncident/d2249f800ffc2577</c>: 364 occurrences over
/// 2026-09-08 → 2026-09-14 across 13 pods, and not one of them names the cause.</para>
///
/// <para>The largest single source of those disposes is an Orleans grain deactivation, which KNOWS
/// its reason — it logs the reason code one line before disposing — and had nowhere to put it.</para>
/// </summary>
public class DirectDisposalAttributionTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly Address Attributed = new("attributed", "1");
    private static readonly Address Unattributed = new("unattributed", "1");
    private static readonly Address Blank = new("blank", "1");
    private static readonly Address FirstCause = new("firstcause", "1");

    private MessageHub Hosted(Address address) =>
        (MessageHub)GetHost().GetHostedHub(address, c => c);

    [Fact]
    public void ADisposerThatNamesItself_IsOnTheAttribution()
    {
        var hub = Hosted(Attributed);

        hub.NoteDirectDisposalBy("Orleans deactivating grain node/x", "ActivationIdle — no recent calls");

        hub.DisposalAttribution.Should().Contain("Orleans deactivating grain node/x");
        hub.DisposalAttribution.Should().Contain("ActivationIdle");
    }

    [Fact]
    public void ADisposerThatSaysNothing_StillReadsAsUnattributed()
        // 🚨 The control for the change itself. If naming a disposer were to become MANDATORY, or if
        // the fallback were replaced by a blank, a teardown nobody claimed would render as `why: `
        // with nothing after it — which reads as "nothing to report" rather than "nobody said".
        => Hosted(Unattributed).DisposalAttribution
            .Should().Contain(MessageHub.DirectDisposeSource);

    [Fact]
    public void ABlankReason_IsReportedAsUnstated_NotAsAnEmptyClause()
    {
        // A disposer may legitimately have no reason; what it must not do is produce an empty one.
        var hub = Hosted(Blank);

        hub.NoteDirectDisposalBy("a caller with nothing to say", "   ");

        hub.DisposalAttribution.Should().Contain("a caller with nothing to say");
        hub.DisposalAttribution.Should().Contain(DisposeRequest.ReasonNotStated);
    }

    [Fact]
    public void FirstCauseWins_SoALaterDisposerCannotOverwriteTheRealOne()
    {
        // 🚨 The property that makes this safe to call on the hottest teardown path in the mesh.
        // A hub already asked to recycle BY NAME keeps that attribution: that request is what
        // actually started its teardown, and Orleans arriving afterwards is a consequence, not the
        // cause. Two disposers racing must not produce a last-writer-wins answer.
        var hub = Hosted(FirstCause);

        hub.NoteDirectDisposalBy("the real cause", "the reason that started it");
        hub.NoteDirectDisposalBy("a later disposer", "a reason that arrived second");

        hub.DisposalAttribution.Should().Contain("the real cause");
        hub.DisposalAttribution.Should().NotContain("a later disposer");
    }
}
