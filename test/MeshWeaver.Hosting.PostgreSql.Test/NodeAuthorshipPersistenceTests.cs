using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Regression: a node's authorship — <see cref="MeshNode.CreatedBy"/>,
/// <see cref="MeshNode.LastModifiedBy"/>, <see cref="MeshNode.CreatedDate"/> — must survive a
/// round-trip through the Postgres storage adapter.
///
/// <para>Before this change <c>mesh_nodes</c> had no authorship columns and the adapter never
/// wrote or read them, so every PG-backed node reloaded with NULL authorship — the node header's
/// "Created by / Updated by" line rendered blank on every instance. (FileSystem/SQLite were
/// unaffected — they serialize the whole node as JSON — so this only reproduces against
/// Postgres.)</para>
/// </summary>
[Collection("PostgreSql")]
public class NodeAuthorshipPersistenceTests
{
    private readonly PostgreSqlFixture _fixture;

    public NodeAuthorshipPersistenceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60000)]
    public async Task Authorship_SurvivesReloadFromStorage()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;
        var created = new DateTimeOffset(2026, 7, 18, 9, 30, 0, TimeSpan.Zero);

        var node = new MeshNode("AuthoredNode")
        {
            Name = "Authored",
            NodeType = "Markdown",
            CreatedBy = "alice@example.com",
            LastModifiedBy = "alice@example.com",
            CreatedDate = created,
        };

        await adapter.Write(node, JsonSerializerOptions.Default).Should().Within(30.Seconds()).Emit();

        var reloaded = await adapter
            .Read("AuthoredNode", JsonSerializerOptions.Default)
            .Should().Within(30.Seconds()).Emit();

        reloaded.Should().NotBeNull("the node was just written");
        reloaded!.CreatedBy.Should().Be("alice@example.com", "creator identity must round-trip");
        reloaded.LastModifiedBy.Should().Be("alice@example.com", "modifier identity must round-trip");
        reloaded.CreatedDate.Should().Be(created, "creation timestamp must round-trip");
    }

    [Fact(Timeout = 60000)]
    public async Task Update_PreservesCreator_RefreshesModifier()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;
        var created = new DateTimeOffset(2026, 7, 18, 9, 30, 0, TimeSpan.Zero);

        await adapter.Write(new MeshNode("Doc")
        {
            Name = "Doc", NodeType = "Markdown",
            CreatedBy = "alice@example.com", LastModifiedBy = "alice@example.com", CreatedDate = created,
        }, JsonSerializerOptions.Default).Should().Within(30.Seconds()).Emit();

        // A later writer (with a DIFFERENT would-be creator) updates the node. created_by /
        // created_date are immutable — the ON CONFLICT SET must not overwrite them — while
        // last_modified_by is refreshed.
        await adapter.Write(new MeshNode("Doc")
        {
            Name = "Doc v2", NodeType = "Markdown",
            CreatedBy = "mallory@example.com", LastModifiedBy = "bob@example.com",
            CreatedDate = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
        }, JsonSerializerOptions.Default).Should().Within(30.Seconds()).Emit();

        var reloaded = await adapter
            .Read("Doc", JsonSerializerOptions.Default)
            .Should().Within(30.Seconds()).Emit();

        reloaded.Should().NotBeNull();
        reloaded!.CreatedBy.Should().Be("alice@example.com", "the original creator is immutable across updates");
        reloaded.CreatedDate.Should().Be(created, "the original creation timestamp is immutable across updates");
        reloaded.LastModifiedBy.Should().Be("bob@example.com", "the last modifier is refreshed on update");
    }

    [Fact(Timeout = 60000)]
    public async Task NodeWithoutAuthorship_ReadsBackNull()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;

        await adapter
            .Write(new MeshNode("Plain") { Name = "plain", NodeType = "Markdown" }, JsonSerializerOptions.Default)
            .Should().Within(30.Seconds()).Emit();

        var reloaded = await adapter
            .Read("Plain", JsonSerializerOptions.Default)
            .Should().Within(30.Seconds()).Emit();

        reloaded.Should().NotBeNull();
        reloaded!.CreatedBy.Should().BeNull();
        reloaded.LastModifiedBy.Should().BeNull();
        reloaded.CreatedDate.Should().Be(default);
    }

    [Fact(Timeout = 60000)]
    public async Task HistoryTrigger_RecordsModifierAsChangedBy()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;

        await adapter.Write(new MeshNode("Tracked")
        {
            Name = "Tracked", NodeType = "Markdown", LastModifiedBy = "carol@example.com",
        }, JsonSerializerOptions.Default).Should().Within(30.Seconds()).Emit();

        // The AFTER INSERT/UPDATE trigger snapshots the row into mesh_node_history with
        // changed_by = NEW.last_modified_by, so version history can attribute each version.
        await using var cmd = _fixture.DataSource.CreateCommand(
            "SELECT changed_by FROM public.mesh_node_history WHERE id = 'Tracked' ORDER BY version DESC LIMIT 1");
        var changedBy = await cmd.ExecuteScalarAsync() as string;

        changedBy.Should().Be("carol@example.com",
            "the history trigger must copy last_modified_by into changed_by so version history shows who made each version");
    }
}
