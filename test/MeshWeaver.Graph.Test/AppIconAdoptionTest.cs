using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The decision an installed-app record's hub makes on initialization: adopt the icon of the app
/// it points at, or leave the record alone.
///
/// <para>🚨 This logic lives on the RECORD'S OWN HUB, and the two rejected alternatives are the
/// point of the test. Repairing icons inside the home's reactive selector re-ran per SUBSCRIPTION
/// — every navigation and reconnect — and ran after the ambient access context was cleared, so its
/// query and writes would have executed with no viewer identity. The hub has neither problem: once
/// per activation, owns the node it writes, unrelated to anyone's page. What still has to be
/// pinned is the DECISION, because every branch of it is a way to get this wrong: overwrite a good
/// icon, rewrite the same placeholder forever, or adopt from a record that points at itself.</para>
/// </summary>
public class AppIconAdoptionTest
{
    private const string Generic = "/static/NodeTypeIcons/puzzlepiece.svg";
    private const string Real = "/static/NodeTypeIcons/chess.svg";

    private static MeshNode Record(string? icon, string? mainNode = "Chess") =>
        MeshNode.FromPath("rbuergi/_App/Chess") with
        {
            NodeType = AppNodeType.NodeType,
            Name = "Chess",
            Icon = icon,
            MainNode = mainNode ?? "rbuergi/_App/Chess",
        };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Generic)]
    public void A_record_without_a_real_icon_adopts_the_apps_own(string? current)
    {
        AppIconAdoption.IconToAdopt(Record(current), Real).Should().Be(Real);
    }

    [Fact]
    public void A_record_that_already_has_a_real_icon_is_left_alone()
    {
        // The Store stamping a real icon must win over this repair, always — including when the
        // repair happens to run afterwards.
        AppIconAdoption.IconToAdopt(Record("/covers/my-own.png"), Real).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Generic)]
    public void A_target_with_no_better_icon_changes_nothing(string? targetIcon)
    {
        // Convergence: rewriting the same placeholder would make every activation a write, and the
        // grid would still look identical. Nothing better available ⇒ nothing happens.
        AppIconAdoption.IconToAdopt(Record(Generic), targetIcon).Should().BeNull();
    }

    [Fact]
    public void A_record_pointing_at_itself_has_nothing_to_adopt_from()
    {
        // MainNode defaults to the node's own path; a record that never got a real target must not
        // resolve itself and copy its own placeholder back.
        AppIconAdoption.TargetOf(Record(Generic, mainNode: "rbuergi/_App/Chess")).Should().BeNull();
    }

    [Fact]
    public void A_record_with_a_real_target_resolves_it()
    {
        AppIconAdoption.TargetOf(Record(Generic)).Should().Be("Chess");
    }

    [Fact]
    public void NeedsIcon_is_the_single_definition_of_generic()
    {
        AppIconAdoption.NeedsIcon(Record(null)).Should().BeTrue();
        AppIconAdoption.NeedsIcon(Record(Generic)).Should().BeTrue();
        AppIconAdoption.NeedsIcon(Record(Generic.ToUpperInvariant())).Should().BeTrue(
            "a path differing only in case is still the placeholder");
        AppIconAdoption.NeedsIcon(Record(Real)).Should().BeFalse();
        AppIconAdoption.NeedsIcon(null).Should().BeFalse("a missing node is not a repair target");
    }

    [Fact]
    public void TheAppNodeType_wires_the_adoption_into_its_hub()
    {
        // The decision above is worthless if nothing calls it: pin that the node type still
        // carries a hub configuration (the initialization hook lives there).
        AppNodeType.CreateMeshNode().HubConfiguration.Should().NotBeNull();
    }
}
