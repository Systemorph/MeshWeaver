using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Regression for the atioz <c>Provider/Anthropic</c> key revert: a node's
/// <see cref="MeshNode.SyncBehavior"/> — the static-repo "Not synced" decouple claim — must
/// survive a round-trip through the Postgres storage adapter.
///
/// <para>Before the <c>sync_behavior</c> column existed, the adapter persisted only the typed
/// content + a fixed set of metadata columns; <c>SyncBehavior</c> was dropped on write and the
/// row→node mapper never read it, so every reload defaulted to
/// <see cref="SyncBehavior.Include"/>. A partition an admin had decoupled therefore silently
/// re-coupled on the next restart, and the next static-repo import re-clobbered the
/// admin-managed key. (Sqlite was unaffected — it serializes the whole node as JSON — so this
/// only reproduces against Postgres.)</para>
/// </summary>
[Collection("PostgreSql")]
public class SyncBehaviorPersistenceTests
{
    private readonly PostgreSqlFixture _fixture;

    public SyncBehaviorPersistenceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A node written with <see cref="SyncBehavior.ExcludeThisAndChildren"/> reads back with the
    /// same claim — the import-skip predicate (<c>ReadClaimedRoots</c>) can only honour a decouple
    /// that storage actually round-trips.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task DecoupleClaim_SurvivesReloadFromStorage()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;

        var node = new MeshNode("DecoupledProvider")
        {
            Name = "Providers",
            NodeType = "Space",
            SyncBehavior = SyncBehavior.ExcludeThisAndChildren,
        };

        await adapter.Write(node, JsonSerializerOptions.Default).Should().Within(30.Seconds()).Emit();

        var reloaded = await adapter
            .Read("DecoupledProvider", JsonSerializerOptions.Default)
            .Should().Within(30.Seconds()).Emit();

        reloaded.Should().NotBeNull("the node was just written");
        reloaded!.SyncBehavior.Should().Be(
            SyncBehavior.ExcludeThisAndChildren,
            "the decouple claim must persist so the static-repo import skips the partition after a restart");
    }

    /// <summary>
    /// A node that never set a sync claim reads back as <see cref="SyncBehavior.Include"/> — the
    /// column default — so existing fully-synced content keeps receiving updates.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task UnclaimedNode_DefaultsToInclude()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;

        await adapter
            .Write(new MeshNode("PlainNode") { Name = "plain", NodeType = "Markdown" }, JsonSerializerOptions.Default)
            .Should().Within(30.Seconds()).Emit();

        var reloaded = await adapter
            .Read("PlainNode", JsonSerializerOptions.Default)
            .Should().Within(30.Seconds()).Emit();

        reloaded.Should().NotBeNull();
        reloaded!.SyncBehavior.Should().Be(SyncBehavior.Include);
    }
}
