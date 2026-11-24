using System.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The portal top bar's <b>Update</b> button is the <c>$Menu:Update</c> slot
/// (<see cref="StaleBuildBanner.UpdateMenuContext"/>): it holds ONE entry iff the hub's offer says a
/// newer build is available, and nothing in every other state. Pure — the projection the instance
/// hub's provider runs on every offer.
/// </summary>
public class StaleBuildUpdateMenuTest
{
    private const string InstancePath = "TestData/StaleAssemblyType/instance1";

    private static StaleBuildOffer Offer(StaleBuildKind kind) =>
        new("TestData/StaleAssemblyType", "v12.dll", "v10.dll") { Kind = kind };

    [Fact]
    public void NoOffer_NoButton()
        => StaleBuildBanner.UpdateMenuItems(InstancePath, null).Should().BeEmpty(
            "an instance on the type's current build must show no Update button — the negative control");

    [Fact]
    public void ServedBuildIsNotPublished_NoButton()
        => StaleBuildBanner.UpdateMenuItems(InstancePath, Offer(StaleBuildKind.ServedBuildIsNotPublished))
            .Should().BeEmpty("a recycle re-binds the same local copy there (#2471), so a button would change nothing");

    [Fact]
    public void NewerBuildAvailable_OneRecycleActionForThisAddress()
    {
        var items = StaleBuildBanner.UpdateMenuItems(InstancePath, Offer(StaleBuildKind.NewerBuildAvailable));
        var item = items.Should().ContainSingle().Which;
        item.Action.Should().Be(MenuActions.Recycle, "the portal runs the ONE page-level recycle flow");
        item.Href.Should().Be("/" + InstancePath, "the action recycles exactly the page's own address");
        item.LabelKey.Should().Be("menu.update");
        item.TooltipKey.Should().Be("menu.updateTooltip");
        item.RequiredPermission.Should().Be(Permission.Read,
            "rebinding to an already-published build is offered to every viewer of the page, not only editors");
    }

    [Fact]
    public void TheMenuContextIsStable()
        => StaleBuildBanner.UpdateMenuContext.Should().Be("Update",
            "the portal shell subscribes $Menu:Update by this literal name");
}
