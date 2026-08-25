using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// 🚨 A NODE WHOSE ID CONTAINS '/' COULD BE READ BUT NEVER DELETED — AND THE FAILURE CLAIMED IT WAS
/// MISSING.
///
/// <para>The storage adapter decomposed a path POSITIONALLY, splitting at the last slash:
/// <c>Provider/OpenRouter/z-ai/glm-5.2</c> became
/// <c>(namespace='Provider/OpenRouter/z-ai', id='glm-5.2')</c> while the row actually stored is
/// <c>(namespace='Provider/OpenRouter', id='z-ai/glm-5.2')</c>. No row matched, so <c>Read</c>
/// answered null and <c>DELETE</c> removed nothing — surfacing as
/// <c>NodeDeletionRejectionReason.NodeNotFound</c> for a node a <c>get</c> resolves in the same
/// breath (issue #2212). The most misleading error shape there is: "not found" sends you looking for
/// a data or a permission problem.</para>
///
/// <para><b>Why it matters beyond one node:</b> every <c>LanguageModel</c> id is the provider's wire
/// id — <c>z-ai/glm-5.3</c>, <c>anthropic/claude-opus-5</c>, <c>openai/gpt-5.2</c> — so NO model node
/// could be removed through the API or MCP at all. Slash-bearing ids are how the platform models
/// vendor-namespaced identifiers, not an accident to design out.</para>
///
/// <para>The fix addresses every row by the stored, indexed <c>path</c> column. These tests assert
/// BOTH directions on each verb: the slash-bearing node is found and removed, AND a path that
/// genuinely has no row is still reported absent — a read or a delete that always succeeds would be
/// no check at all.</para>
/// </summary>
[Collection("PostgreSql")]
public class SlashBearingIdCrudTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    private readonly PostgreSqlFixture _fixture = fixture;
    private readonly JsonSerializerOptions _options = new();

    /// <summary>The exact shape every AI model node is in: a vendor-namespaced id under a provider.</summary>
    private const string Namespace = "Provider/OpenRouter";
    private const string SlashId = "z-ai/glm-5.2";
    private const string SlashPath = "Provider/OpenRouter/z-ai/glm-5.2";

    /// <summary>A sibling with NO slash, so the assertions can prove the fix did not simply widen
    /// matching to "anything under the namespace".</summary>
    private const string PlainPath = "Provider/OpenRouter/plain-model";

    private async Task<PostgreSqlStorageAdapter> SeedAsync(CancellationToken ct)
    {
        await _fixture.CleanDataAsync();
        var partitionDef = new PartitionDefinition
        {
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        var (_, adapter) = await _fixture.CreateSchemaAdapterAsync(
            "provider", partitionDef with { Namespace = "Provider", Schema = "provider" });

        await adapter.WriteAsync(
            new MeshNode(SlashId, Namespace)
            {
                Name = "GLM 5.2", NodeType = "LanguageModel", State = MeshNodeState.Active
            }, _options, ct);
        await adapter.WriteAsync(
            new MeshNode("plain-model", Namespace)
            {
                Name = "Plain", NodeType = "LanguageModel", State = MeshNodeState.Active
            }, _options, ct);
        return adapter;
    }

    [Fact(Timeout = 120000)]
    public async Task Read_Exists_ReadMany_AllResolve_ANodeWhoseIdContainsASlash()
    {
        var ct = TestContext.Current.CancellationToken;
        var adapter = await SeedAsync(ct);

        var node = await adapter.ReadAsync(SlashPath, _options, ct);
        node.Should().NotBeNull(
            "the row is stored as (namespace='Provider/OpenRouter', id='z-ai/glm-5.2'); a positional "
            + "split at the last slash looks under a namespace that has no rows at all (#2212)");
        node!.Path.Should().Be(SlashPath);
        node.Id.Should().Be(SlashId, "the id keeps its vendor prefix — that IS the model's wire id");

        (await adapter.ExistsAsync(SlashPath, ct)).Should().BeTrue();

        // ReadMany carried the same defect in batched form: it grouped by (table, namespace) and
        // matched `id IN (...)`, so a slash-bearing path was looked up under the wrong namespace and
        // silently contributed no row to the batch.
        var batch = await adapter.ReadMany([SlashPath, PlainPath], _options)
            .ToList().FirstAsync().ToTask(ct);
        batch.Select(n => n.Path).Order().Should().Equal(PlainPath, SlashPath);
    }

    /// <summary>
    /// The other direction, on the SAME verbs: a path with no row must still read as absent. Without
    /// this the fix could not be distinguished from "match anything", and the read would stop being a
    /// check at all.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task Read_Exists_StillReportAbsent_ForAPathWithNoRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var adapter = await SeedAsync(ct);

        // The positional split's phantom decomposition: nothing is stored at this path.
        (await adapter.ReadAsync($"{Namespace}/z-ai", _options, ct)).Should().BeNull(
            "no node lives at the vendor segment — it is part of the child's id");
        (await adapter.ExistsAsync($"{Namespace}/z-ai", ct)).Should().BeFalse();
        (await adapter.ReadAsync($"{Namespace}/z-ai/does-not-exist", _options, ct)).Should().BeNull();
        (await adapter.ReadMany([$"{Namespace}/z-ai"], _options).ToList().FirstAsync().ToTask(ct))
            .Should().BeEmpty();
    }

    [Fact(Timeout = 120000)]
    public async Task Delete_RemovesANodeWhoseIdContainsASlash_AndLeavesItsSiblingsAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var adapter = await SeedAsync(ct);

        var removed = await adapter.DeleteIfExists(SlashPath).FirstAsync().ToTask(ct);
        removed.Should().BeTrue(
            "the DELETE matched no row before the fix, which the mesh reports as "
            + "NodeDeletionRejectionReason.NodeNotFound — for a node Read resolves (#2212)");

        (await adapter.ExistsAsync(SlashPath, ct)).Should().BeFalse(
            "the row must actually be gone, not merely reported gone");
        (await adapter.ExistsAsync(PlainPath, ct)).Should().BeTrue(
            "addressing by path must delete exactly one row — never the whole namespace");

        // And the negative direction: deleting again (or deleting a phantom) reports false rather
        // than claiming a removal that did not happen.
        (await adapter.DeleteIfExists(SlashPath).FirstAsync().ToTask(ct)).Should().BeFalse(
            "a second delete removes nothing and must say so");
        (await adapter.DeleteIfExists($"{Namespace}/z-ai").FirstAsync().ToTask(ct)).Should().BeFalse(
            "the vendor segment is not a node");

        output.WriteLine($"deleted {SlashPath}; sibling {PlainPath} intact");
    }
}
