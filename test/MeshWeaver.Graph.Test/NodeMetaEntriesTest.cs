using System;
using System.Linq;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The node header's provenance line — <c>Type · Created · Updated</c> — and the third state it
/// could not express.
///
/// <para>The line is what every Markdown page shows and no custom page does: it rides on
/// <see cref="MeshNodeLayoutAreas.BuildHeader(LayoutAreaHost, MeshNode?, bool)"/>, so a node type
/// that renders its OWN default area (a CRM client, a board) silently ships without it. Making the
/// segments a PURE function of the node, the viewer's zone and an optional activity override is
/// what lets a module render the same line — and lets this fixture assert it without a mesh, a
/// host, or a mock.</para>
///
/// <para>🚨 <b>Why the override exists.</b> On a CONTAINER node the node's own
/// <c>LastModified</c> is the wrong answer and reads as a false one. A CRM client root is written
/// when the account is opened and then barely again — the engagement lives in its children — so
/// its own timestamp reports the migration that retyped it, by <c>system-security</c>, while the
/// deals and documents underneath moved days later. "Updated 2026-08-28 by system-security" on a
/// page whose account was worked yesterday is not a missing feature; it is a wrong statement. The
/// override replaces that segment with the newest activity across the partition, which is the
/// question the page is actually asked.</para>
/// </summary>
public class NodeMetaEntriesTest
{
    private const string Zone = "Europe/Zurich";

    private static MeshNode Node(
        DateTimeOffset? created = null,
        string? createdBy = null,
        DateTimeOffset? modified = null,
        string? modifiedBy = null,
        string nodeType = "Crm/Client") =>
        new("SchenkerLabs")
        {
            Name = "Schenker Labs",
            NodeType = nodeType,
            CreatedDate = created ?? default,
            CreatedBy = createdBy,
            LastModified = modified ?? default,
            LastModifiedBy = modifiedBy,
        };

    [Fact]
    public void The_three_segments_are_type_created_and_updated()
    {
        var entries = MeshNodeLayoutAreas.BuildMetaEntries(
            Node(
                created: new DateTimeOffset(2026, 8, 27, 7, 59, 0, TimeSpan.Zero), createdBy: "sglauser",
                modified: new DateTimeOffset(2026, 8, 28, 14, 30, 0, TimeSpan.Zero), modifiedBy: "system-security"),
            Zone);

        entries.Select(e => e.LabelKey).Should().Equal(
            MeshNodeLayoutAreas.MetaTypeKey,
            MeshNodeLayoutAreas.MetaCreatedKey,
            MeshNodeLayoutAreas.MetaUpdatedKey);
    }

    [Fact]
    public void The_type_segment_links_to_the_types_configuration()
    {
        var type = MeshNodeLayoutAreas.BuildMetaEntries(Node(), Zone).Single(e => e.LabelKey == MeshNodeLayoutAreas.MetaTypeKey);

        // The label is the LAST segment — "Client", not "Crm/Client" — and it navigates to the type.
        type.Text.Should().Be("Client");
        type.Href.Should().Be($"/Crm/Client/{NodeTypeLayoutAreas.ConfigurationArea}");
    }

    [Fact]
    public void Timestamps_render_in_the_viewers_zone_with_the_author()
    {
        // 07:59 UTC on 27 August is 09:59 in Zurich (CEST, UTC+2). A UTC render would read 07:59
        // and be wrong for every viewer in the office.
        var created = MeshNodeLayoutAreas
            .BuildMetaEntries(Node(created: new DateTimeOffset(2026, 8, 27, 7, 59, 0, TimeSpan.Zero), createdBy: "sglauser"), Zone)
            .Single(e => e.LabelKey == MeshNodeLayoutAreas.MetaCreatedKey);

        created.Text.Should().Be("2026-08-27 09:59 by sglauser");
    }

    [Fact]
    public void An_absent_author_leaves_no_dangling_by()
    {
        var created = MeshNodeLayoutAreas
            .BuildMetaEntries(Node(created: new DateTimeOffset(2026, 8, 27, 7, 59, 0, TimeSpan.Zero)), Zone)
            .Single(e => e.LabelKey == MeshNodeLayoutAreas.MetaCreatedKey);

        created.Text.Should().Be("2026-08-27 09:59");
    }

    [Fact]
    public void An_unset_timestamp_emits_no_segment_at_all()
    {
        // Not "—", not the epoch: a node with no recorded date says nothing rather than something false.
        var entries = MeshNodeLayoutAreas.BuildMetaEntries(Node(), Zone);

        entries.Should().NotContain(e => e.LabelKey == MeshNodeLayoutAreas.MetaCreatedKey);
        entries.Should().NotContain(e => e.LabelKey == MeshNodeLayoutAreas.MetaUpdatedKey);
    }

    [Fact]
    public void A_null_node_yields_no_segments()
    {
        MeshNodeLayoutAreas.BuildMetaEntries(null, Zone).Should().BeEmpty();
    }

    [Fact]
    public void The_node_type_node_itself_carries_no_type_segment()
    {
        // A NodeType page linking to its own Configuration area is a link to the page you are on.
        var entries = MeshNodeLayoutAreas.BuildMetaEntries(Node(nodeType: MeshNode.NodeTypePath), Zone);

        entries.Should().NotContain(e => e.LabelKey == MeshNodeLayoutAreas.MetaTypeKey);
    }

    // ---- The override: a container reports its partition's activity, not its own row's --------

    [Fact]
    public void An_activity_override_replaces_the_updated_segment()
    {
        var entries = MeshNodeLayoutAreas.BuildMetaEntries(
            Node(
                created: new DateTimeOffset(2026, 8, 27, 7, 59, 0, TimeSpan.Zero), createdBy: "sglauser",
                modified: new DateTimeOffset(2026, 8, 28, 14, 30, 0, TimeSpan.Zero), modifiedBy: "system-security"),
            Zone,
            new NodeActivity(new DateTimeOffset(2026, 9, 8, 11, 5, 0, TimeSpan.Zero), "mkleiner", "Reply from Jörg"));

        entries.Select(e => e.LabelKey).Should().Equal(
            MeshNodeLayoutAreas.MetaTypeKey,
            MeshNodeLayoutAreas.MetaCreatedKey,
            MeshNodeLayoutAreas.MetaLastActivityKey);

        // 🚨 The whole point: the migration's stamp must NOT survive alongside the real answer.
        entries.Should().NotContain(e => e.LabelKey == MeshNodeLayoutAreas.MetaUpdatedKey);
        entries.Single(e => e.LabelKey == MeshNodeLayoutAreas.MetaLastActivityKey)
            .Text.Should().Be("2026-09-08 13:05 by mkleiner — Reply from Jörg");
    }

    [Fact]
    public void An_override_stands_in_for_an_updated_segment_that_was_never_there()
    {
        // A container whose own row has no LastModified still has activity underneath it.
        var entries = MeshNodeLayoutAreas.BuildMetaEntries(
            Node(created: new DateTimeOffset(2026, 8, 27, 7, 59, 0, TimeSpan.Zero), createdBy: "sglauser"),
            Zone,
            new NodeActivity(new DateTimeOffset(2026, 9, 8, 11, 5, 0, TimeSpan.Zero), "mkleiner"));

        entries.Single(e => e.LabelKey == MeshNodeLayoutAreas.MetaLastActivityKey)
            .Text.Should().Be("2026-09-08 13:05 by mkleiner");
    }

    [Fact]
    public void An_override_with_no_activity_yet_falls_back_to_the_nodes_own_row()
    {
        // An empty client — nothing recorded under it — must not read as "no activity ever",
        // which would erase the fact that the account was created at all.
        var entries = MeshNodeLayoutAreas.BuildMetaEntries(
            Node(
                created: new DateTimeOffset(2026, 8, 27, 7, 59, 0, TimeSpan.Zero), createdBy: "sglauser",
                modified: new DateTimeOffset(2026, 8, 28, 14, 30, 0, TimeSpan.Zero), modifiedBy: "system-security"),
            Zone,
            lastActivity: null);

        entries.Select(e => e.LabelKey).Should().Contain(MeshNodeLayoutAreas.MetaUpdatedKey);
    }
}
