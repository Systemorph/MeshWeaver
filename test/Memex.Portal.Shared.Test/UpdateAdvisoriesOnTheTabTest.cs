using System;
using Memex.Portal.Shared.SelfUpdate;
using Memex.Portal.Shared.Settings;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>What the release gate reported WITHOUT holding is a state on the Updates tab (#3651).</b>
///
/// <para>A roll that recompiles a course at boot, or that carries a module whose loadability on
/// the target could not be measured, is a roll an operator must be able to SEE on the surface they
/// already look at — not a fact that lives only in a pod log. The poller records the verdict's
/// advisories on <c>Admin/UpdatePolicy</c> beside the (cleared or set) hold, and the tab renders
/// them for the tag they describe and for no other.</para>
/// </summary>
public class UpdateAdvisoriesOnTheTabTest
{
    private const string Tag = "3.0.0-ci.8100";

    [Fact]
    public void Advisories_RenderForTheTagTheyDescribe_UnderTheAvailableLine()
    {
        var content = new UpdatePolicyContent
        {
            LatestAvailableTag = Tag,
            CheckedAt = DateTimeOffset.UtcNow,
            AdvisoriesTag = Tag,
            Advisories = "would recompile at boot on 3.0.0-ci.8100: Education, Crm; "
                         + "Views: whether its landed module MeshWeaver.Views loads could not be determined",
        };

        var markdown = UpdatePolicySettingsTab.StatusMarkdown(content, EchoLocalizer);

        Assert.Contains($"ui.updateLatestAvailable[{Tag}]", markdown, StringComparison.Ordinal);
        Assert.Contains($"ui.updateAdvisories[{Tag}]", markdown, StringComparison.Ordinal);
        Assert.Contains("would recompile at boot", markdown, StringComparison.Ordinal);
        Assert.Contains("MeshWeaver.Views", markdown, StringComparison.Ordinal);
        // Not held: an advisory is a cost the roll accepts, and must never read as a hold.
        Assert.DoesNotContain("ui.updateHeld[", markdown, StringComparison.Ordinal);
    }

    /// <summary>Advisories recorded about a PREVIOUS candidate say nothing about this one — a
    /// stale scare is the same defect as a stale hold.</summary>
    [Fact]
    public void Advisories_AboutAnotherTag_AreNotRendered()
    {
        var content = new UpdatePolicyContent
        {
            LatestAvailableTag = Tag,
            CheckedAt = DateTimeOffset.UtcNow,
            AdvisoriesTag = "3.0.0-ci.8055",
            Advisories = "would recompile at boot on 3.0.0-ci.8055: Education",
        };

        var markdown = UpdatePolicySettingsTab.StatusMarkdown(content, EchoLocalizer);

        Assert.DoesNotContain("ui.updateAdvisories", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Education", markdown, StringComparison.Ordinal);
        Assert.False(content.HasAdvisoriesFor(Tag));
    }

    /// <summary>A held tag renders its hold AND its advisories — the two answer different
    /// questions (why it is not moving; what it would cost when it does) and neither hides the other.</summary>
    [Fact]
    public void AHeldTag_RendersBothTheHoldAndTheAdvisories()
    {
        var content = new UpdatePolicyContent
        {
            LatestAvailableTag = Tag,
            CheckedAt = DateTimeOffset.UtcNow,
            HeldTag = Tag,
            HeldReason = "Views: its landed module MeshWeaver.Views cannot load on 3.0.0-ci.8100",
            HeldAt = DateTimeOffset.UtcNow,
            AdvisoriesTag = Tag,
            Advisories = "would recompile at boot on 3.0.0-ci.8100: Education",
        };

        var markdown = UpdatePolicySettingsTab.StatusMarkdown(content, EchoLocalizer);

        Assert.Contains($"ui.updateHeld[{Tag}]", markdown, StringComparison.Ordinal);
        Assert.Contains("cannot load", markdown, StringComparison.Ordinal);
        Assert.Contains($"ui.updateAdvisories[{Tag}]", markdown, StringComparison.Ordinal);
        Assert.Contains("Education", markdown, StringComparison.Ordinal);
    }

    private static string EchoLocalizer(string key, object?[] args) =>
        $"{key}[{string.Join(',', args)}]";
}
