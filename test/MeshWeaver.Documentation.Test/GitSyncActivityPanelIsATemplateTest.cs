using System.Linq;
using MeshWeaver.Graph;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// The GitHub-sync tab's activity panel is a TEMPLATE (Doc/GUI/DataBinding → "Templates first,
/// data later").
///
/// <para><b>What it was.</b> <c>BuildActivityPanel</c> subscribed the activity node TWICE on the
/// tab's hub and rebuilt hand-made HTML (status + last 8 log lines) and a Cancel button out of every
/// progress tick: the panel was a deferred view that rendered nothing until the activity hub
/// answered, and a parallel copy of the activity's own progress view.</para>
///
/// <para><b>What this pins.</b> The panel embeds the activity node's OWN
/// <see cref="ActivityLayoutAreas.ProgressArea"/> (log, status, Cancel — rendered by the activity's
/// hub) with a skeleton loading shape, and holds no deferred view of its own.</para>
///
/// <para>Negative control, run before this file was committed: one deferred view —
/// <c>WithView((h, _) =&gt; observable)</c>, the shape the old panel was built from — added to the
/// panel fails <see cref="LayoutTemplateAssertions.EveryViewIsStatic"/> ("StackControl carries 1
/// deferred view(s)").</para>
/// </summary>
public class GitSyncActivityPanelIsATemplateTest
{
    [Fact]
    public void ThePanelEmbedsTheActivitysOwnProgressArea_AndWaitsOnNothing()
    {
        const string activityPath = "acme/Space/_Activity/sync-1";

        var panel = GitHubSyncSettingsTab.BuildActivityPanel(activityPath);

        LayoutTemplateAssertions.EveryViewIsStatic(panel);
        var progress = Assert.Single(LayoutTemplateAssertions.Descendants(panel).OfType<LayoutAreaControl>());
        Assert.Equal(activityPath, progress.Address.ToString());
        Assert.Equal(ActivityLayoutAreas.ProgressArea, progress.Reference.Area);
        Assert.Equal(SpinnerType.Skeleton, progress.SpinnerType);
    }

    [Fact]
    public void WithNoOperationRunning_ThePanelIsEmpty()
    {
        var panel = GitHubSyncSettingsTab.BuildActivityPanel(null);

        Assert.Single(LayoutTemplateAssertions.Descendants(panel));
    }
}
