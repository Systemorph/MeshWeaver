using System;
using Memex.Portal.Shared.SelfUpdate;
using Memex.Portal.Shared.Settings;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A hold RECORD is not a live verdict (#3812).</b>
///
/// <para><see cref="UpdatePolicyContent.HeldAt"/> / <c>HeldReason</c> are written only when a
/// candidate is actually EVALUATED; <c>LastCheckedAt</c> is written on EVERY check. With updates
/// switched off the check records "updates are disabled" and evaluates nothing, so the hold is
/// never revisited and never cleared — while the fresh <c>LastCheckedAt</c> beside it makes a
/// days-old record read as the reason the install is standing still.</para>
///
/// <para>Measured on memex 2026-09-09: <c>heldAt</c> 2026-09-07T22:27Z against <c>lastCheckedAt</c>
/// 2026-09-09T10:41Z, and the frozen reason quoted three module floors that #3648 had stopped
/// holding on TWO HOURS after that hold was written. #3706 was filed on that record and read it as
/// current. This suite is the control that could have caught it.</para>
///
/// <para>🚨 What is deliberately NOT tested, because it must not happen: the hold being cleared when
/// updates are switched off. The last real evaluation is the only diagnostic an operator has before
/// turning updates back on, so it is kept — and shown as history.</para>
/// </summary>
public class FrozenHoldIsHistoryTest
{
    private const string Tag = "3.0.0-ci.8057";
    private const string Running = "3.0.0-ci.8000";
    private const string Reason = "Agent: the module requires platform 3.0.0 or newer";

    private static string EchoLocalizer(string key, object?[] args) =>
        $"{key}[{string.Join(',', args)}]";

    private static UpdatePolicyContent Held(UpdatePolicyKind policy, bool dated = true) =>
        new()
        {
            Policy = policy,
            LatestAvailableTag = Tag,
            HeldTag = Tag,
            HeldReason = Reason,
            HeldAt = dated ? new DateTimeOffset(2026, 9, 7, 22, 27, 17, TimeSpan.Zero) : null,
            // The field that made the frozen record look current: a check ran 36 h later and
            // recorded only that it was not allowed to evaluate anything.
            LastCheckedAt = new DateTimeOffset(2026, 9, 9, 10, 41, 24, TimeSpan.Zero),
            LastCheckVerdict = "updates are disabled on this install (Admin/UpdatePolicy = None)",
        };

    [Theory]
    [InlineData(UpdatePolicyKind.Continuous)]
    [InlineData(UpdatePolicyKind.Stable)]
    public void WithCheckingON_TheHoldIsOperative(UpdatePolicyKind policy) =>
        Held(policy).IsHoldOperative(Tag).Should().BeTrue(
            "a poller that is running would clear this hold the moment the candidate resolves, so "
            + "it is a live verdict");

    [Fact]
    public void WithCheckingOFF_TheHoldIsRecordedButNotOperative()
    {
        var content = Held(UpdatePolicyKind.None);

        content.IsHeld(Tag).Should().BeTrue("the record is still there, and still worth showing");
        content.IsHoldOperative(Tag).Should().BeFalse(
            "nothing evaluates while updates are off, so this verdict can neither be reaffirmed "
            + "nor cleared — it is history, not the reason the install is standing still");
    }

    [Fact]
    public void AnOperativeHold_StillReadsAsAHold()
    {
        var markdown = UpdatePolicySettingsTab.StatusMarkdown(
            Held(UpdatePolicyKind.Continuous), EchoLocalizer);

        markdown.Should().Contain($"ui.updateHeld[{Tag}]",
            "with checking on, the hold IS why this install is not moving");
        markdown.Should().Contain("ui.updateHeldAt[");
        markdown.Should().NotContain("ui.updateHoldHistorical");
        markdown.Should().Contain(Reason, "the reason is quoted either way");
    }

    [Fact]
    public void AFrozenHold_ReadsAsHistory_NeverAsTheCurrentVerdict()
    {
        var markdown = UpdatePolicySettingsTab.StatusMarkdown(
            Held(UpdatePolicyKind.None), EchoLocalizer);

        markdown.Should().Contain("ui.updateHoldHistorical[",
            "the record must announce itself as a record");
        markdown.Should().Contain("2026-09-07 22:27",
            "and carry the moment it was written, which is the fact a reader needs to judge it");
        markdown.Should().NotContain($"ui.updateHeld[{Tag}]",
            "'update is held' states an ongoing refusal; nothing is refusing anything here");
        markdown.Should().NotContain("ui.updateHeldUnknown");
        markdown.Should().NotContain("ui.updateHeldAt[",
            "'(held <date>)' reads as an ongoing state and is dropped with the live framing");
        markdown.Should().Contain(Reason,
            "🚨 the reason is still shown — it is the only diagnostic an operator has before "
            + "switching updates back on. Only its FRAMING changes");
    }

    [Fact]
    public void AFrozenHoldWithNoTimestamp_StillReadsAsHistory()
    {
        var markdown = UpdatePolicySettingsTab.StatusMarkdown(
            Held(UpdatePolicyKind.None, dated: false), EchoLocalizer);

        markdown.Should().Contain($"ui.updateHoldHistoricalUndated[{Tag}]",
            "a record with no timestamp is still a record, and the undated wording says so rather "
            + "than falling back to the live phrasing");
        markdown.Should().NotContain($"ui.updateHeld[{Tag}]");
    }

    [Fact]
    public void TheAboutSurface_DoesNotReportAFrozenHoldAsHeld()
    {
        var status = PlatformUpdateStatus.Derive(Held(UpdatePolicyKind.None), Running);

        status.Availability.Should().NotBe(PlatformUpdateAvailability.UpdateHeld,
            "rendering 'update held' off a note nothing can refresh names an incompatibility that "
            + "may have been fixed days ago — which is exactly how #3706 was filed");
    }

    [Fact]
    public void TheAboutSurface_StillReportsAnOperativeHold()
    {
        var status = PlatformUpdateStatus.Derive(Held(UpdatePolicyKind.Continuous), Running);

        status.Availability.Should().Be(PlatformUpdateAvailability.UpdateHeld,
            "an install that is actively refusing a build must not look like one about to take it");
    }

    /// <summary>
    /// 🚨 The positive control on the OTHER half. A Red combo verification is a recorded FACT about
    /// the build, not a note about the poller — so it keeps holding even with checking off, and a
    /// change that made "checking is off" swallow every refusal would fail here.
    /// </summary>
    [Fact]
    public void ARedComboVerdict_StillHolds_EvenWithCheckingOff()
    {
        var content = new UpdatePolicyContent
        {
            Policy = UpdatePolicyKind.None,
            LatestAvailableTag = Tag,
            ComboVerifications =
            [
                new ComboVerification
                {
                    CandidateTag = Tag,
                    Verdict = ComboVerdictKind.Red,
                },
            ],
        };

        PlatformUpdateStatus.Derive(content, Running).Availability
            .Should().Be(PlatformUpdateAvailability.UpdateHeld,
                "the verdict is the fact; only the poller's NOTE about a hold goes stale");
    }
}
