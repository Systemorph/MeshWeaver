using System.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The Threads app page (<c>/{user}/Chat</c>, <see cref="UserActivityLayoutAreas.BuildThreadsApp"/>)
/// — the agentic-app default view: ONE <see cref="ThreadChatControl"/> in node-less compact mode
/// with the collapsible threads side menu turned on. The menu, its live status rows
/// (evaluating / queued / awaiting input), the filter box and the collapse toggle all live in the
/// Blazor chat view, bound through the synced GetQuery cache. These tests pin the regressions
/// that made the first Threads app fail:
/// <list type="bullet">
/// <item>Rail rows delegated to a <c>RailItem</c> item area on each THREAD's own hub — one hub
/// activation per result, resolving an area on a hub the page does not own: "area cannot be
/// found" in the distributed portal while passing in a monolith. The page is deliberately NOT a
/// search control any more, so there is no item area left to point at a foreign hub.</item>
/// <item>The old MDI shell stretched the compact composer to <c>height: 100%</c>, turning the
/// input into a viewport-height empty gray box.</item>
/// </list>
/// </summary>
public class ThreadsAppShapeTest
{
    [Fact]
    public void Page_IsOneChatSurface_NoSearchRail()
    {
        var page = UserActivityLayoutAreas.BuildThreadsApp()
            .Should().BeOfType<StackControl>().Subject;

        // 🚨 The regression guard: the page hosts exactly ONE view — the chat surface. The old
        // page was a shell whose rail (a search control with a per-result RailItem area) resolved
        // areas on foreign thread hubs and failed distributed.
        page.Areas.Should().HaveCount(1, "the chat surface carries the side menu natively");
        page.Should().NotBeOfType<MeshSearchControl>();
    }

    [Fact]
    public void Composer_IsNodelessCompact_WithTheThreadsSideMenuOn()
    {
        var composer = UserActivityLayoutAreas.ThreadsAppComposer();

        composer.HideEmptyState.Should().BeTrue(
            "the node-less composer is compact — a thread only exists after Send, which opens it full-screen");
        composer.ShowThreadNav.Should().BeTrue(
            "the collapsible threads side menu is the default view when working with threads");
        composer.ThreadPath.Should().BeNull("the page is node-less");
    }

    [Fact]
    public void NothingForcesAHeightOnTheCompactComposer()
    {
        // The old MDI shell's `height: 100%` chain turned the compact composer into a
        // viewport-height empty box. The wrapper may flex-fill, but no forced 100% height.
        UserActivityLayoutAreas.BuildThreadsApp().Style?.ToString().Should().NotContain("height: 100%");
        UserActivityLayoutAreas.ThreadsAppComposer().Style?.ToString().Should().NotContain("height: 100%");
    }
}
