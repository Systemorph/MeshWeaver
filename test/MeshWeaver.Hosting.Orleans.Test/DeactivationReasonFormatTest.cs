using MeshWeaver.Hosting.Orleans;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 The clause the grain hands to the hub's teardown attribution (#4888 / #4960 review).
///
/// <para>The attribution tests live on the HUB side and exercise
/// <c>MessageHub.NoteDirectDisposalBy</c> directly. The handoff — this formatting, and the call in
/// <c>OnDeactivateAsync</c> — is a separate thing that could regress with all of them green, which
/// is what review pointed out. This closes the formatting half.</para>
///
/// <para>🚨 Stated plainly, because a partial cover reported as a full one is worse than none: the
/// CALL SITE is still uncovered. Nothing here would catch someone deleting the
/// <c>NoteDirectDisposalBy</c> line from <c>OnDeactivateAsync</c>. Driving a real Orleans
/// deactivation and reading the resulting attribution is the test that would, and it is named as
/// missing on the pull request rather than approximated here.</para>
/// </summary>
public class DeactivationReasonFormatTest
{
    [Fact]
    public void TheReasonCodeIsAlwaysPresent()
        => MessageHubGrain.FormatDeactivationReason("ActivationIdle", "no recent calls")
            .Should().StartWith("ActivationIdle");

    [Fact]
    public void ADescriptionIsAppendedWhenThereIsOne()
        => MessageHubGrain.FormatDeactivationReason("ShuttingDown", "silo is stopping")
            .Should().Be("ShuttingDown — silo is stopping");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoDescription_LeavesNoTrailingSeparator(string? absent)
        // 🚨 The half that matters. "ActivationIdle — " reads to a human as a sentence that was
        // CUT OFF, not as one with nothing to add — the same "renders as nothing, reads as
        // something missing" failure the blank-reason normalisation on the hub side exists to
        // prevent. Orleans supplies no description for several reason codes, so this is the
        // ordinary case rather than an edge one.
        => MessageHubGrain.FormatDeactivationReason("ActivationIdle", absent)
            .Should().Be("ActivationIdle");
}
