using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the cure for agentic-pensions#12 ("virtual-data-source workspaces stay empty on the
/// deployed portal"): <see cref="MeshNodeContentExtensions.ContentAs{T}"/> must recover
/// <see cref="MeshNode.Content"/> that was materialized as a SAME-NAMED type from a DIFFERENT
/// assembly.
///
/// <para>The production chain this reproduces: every dynamically compiled NodeType assembly
/// carries its own copy of shared records (<c>@@</c>-included sources), so CLR identity across
/// those assemblies never matches. The query layer (<c>IMeshService.Query&lt;MeshNode&gt;</c>)
/// deserializes rows with the ROOT hub's options; the root registry auto-learns the short-name
/// <c>$type</c> the first time such content is SERIALIZED at root level (persistence writes —
/// <c>PolymorphicTypeInfoResolver.ComputeDerivedTypes</c> → <c>GetOrAddType</c>), and from then
/// on every query result's Content arrives as the OWNING NodeType assembly's CLR type. A consumer
/// hub's <c>ContentAs&lt;T&gt;</c> — where <c>T</c> is its OWN compiled copy — then hit the
/// <c>default:</c> branch (not <c>T</c>, not <see cref="JsonElement"/>) and silently returned
/// null: every content-filtered loader row vanished and the dashboards rendered their empty
/// state (atioz: <c>AgenticPension/Dashboard</c> EntryNode = 0 of 200, ExtraktionsReview
/// "Noch keine Bilanzwerte geladen"), while metadata-only collections kept working.</para>
///
/// <para>The contract (per the ContentAs xmldoc): the read site owns recovery into its target
/// type. A same-shaped foreign-assembly instance carries the authoritative JSON shape — recover
/// it by round-tripping through JSON, exactly like the degraded-JsonElement path.</para>
/// </summary>
public class ContentAsForeignAssemblyContentTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// The "query layer's" MeshNode: Content serialized and rehydrated through the HOST hub's
    /// options. Serializing auto-registers the short-name $type in the host registry (the exact
    /// write-side pollution), so the rehydrate materializes Content as
    /// <see cref="QueryLayerModel.BalanceEntry"/> — the foreign type every consumer receives.
    /// </summary>
    private MeshNode MaterializeThroughHost(out JsonSerializerOptions hostOptions)
    {
        var host = GetHost();
        hostOptions = host.JsonSerializerOptions;

        var typedNode = MeshNode.FromPath("pension/Entry/e1") with
        {
            Name = "Aktien 2024",
            NodeType = "pension/Entry",
            Content = new QueryLayerModel.BalanceEntry
            {
                Position = "pension/Position/Equities",
                Year = "pension/Year/2024",
                Amount = 16091.584,
                Konfidenz = QueryLayerModel.Confidence.Mittel,
            },
        };

        var json = JsonSerializer.Serialize(typedNode, hostOptions);
        var node = JsonSerializer.Deserialize<MeshNode>(json, hostOptions);
        node.Should().NotBeNull();
        return node!;
    }

    [Fact]
    public void ContentAs_SameNamedTypeFromForeignAssembly_RecoversViaJsonRoundTrip()
    {
        var node = MaterializeThroughHost(out _);

        // Premise of the repro: the query layer hands consumers Content ALREADY MATERIALIZED
        // as the foreign CLR type (not a JsonElement). If this ever degrades to JsonElement
        // the test no longer exercises the defect — fail loudly so it gets rebuilt.
        node.Content.Should().BeOfType<QueryLayerModel.BalanceEntry>(
            "the host-side rehydrate must reproduce the query layer's typed materialization");

        // The consumer hub types the content with ITS OWN copy of the record — same short
        // name, different CLR type (prod: a different DynamicNode_* assembly).
        var client = GetClient();
        var typed = node.ContentAs<ConsumerModel.BalanceEntry>(client.JsonSerializerOptions);

        typed.Should().NotBeNull(
            "ContentAs must recover a same-shaped foreign-assembly Content instead of silently dropping it");
        typed!.Position.Should().Be("pension/Position/Equities");
        typed.Year.Should().Be("pension/Year/2024");
        typed.Amount.Should().Be(16091.584);
        typed.Konfidenz.Should().Be(ConsumerModel.Confidence.Mittel);
    }

    [Fact]
    public void ContentAs_DifferentlyNamedTargetType_BindsByShapeLikeTheJsonElementPath()
    {
        var node = MaterializeThroughHost(out _);
        node.Content.Should().BeOfType<QueryLayerModel.BalanceEntry>();

        // Parity with the degraded-JsonElement recovery: a $type the target doesn't recognise
        // is just an ignored member — the fields still bind by shape (the rbuergi/EntryProbe
        // diagnostic on atioz exercised exactly this: ContentAs<ProbeEntry> over
        // "$type":"BalanceSheetEntry" content).
        var client = GetClient();
        var typed = node.ContentAs<ConsumerModel.RenamedEntry>(client.JsonSerializerOptions);

        typed.Should().NotBeNull();
        typed!.Position.Should().Be("pension/Position/Equities");
        typed.Year.Should().Be("pension/Year/2024");
    }

    [Fact]
    public void ContentAs_ForeignRecovery_DoesNotBreakTheConsumersOwnRegistrations()
    {
        var node = MaterializeThroughHost(out _);
        var client = GetClient();

        // Recover the foreign content (this may auto-register names in the client registry) …
        node.ContentAs<ConsumerModel.BalanceEntry>(client.JsonSerializerOptions).Should().NotBeNull();

        // … and the client's OWN type must still round-trip through the client's options
        // afterwards: the recovery must not clobber the consumer's name→type resolution.
        var own = new ConsumerModel.BalanceEntry
        {
            Position = "pension/Position/Cash",
            Year = "pension/Year/2025",
            Amount = 1.0,
        };
        var json = JsonSerializer.Serialize<object>(own, client.JsonSerializerOptions);
        var back = JsonSerializer.Deserialize<object>(json, client.JsonSerializerOptions);
        back.Should().BeOfType<ConsumerModel.BalanceEntry>(
            "the consumer's own registration must survive the foreign-content recovery");
    }
}

/// <summary>The record shape as compiled into the OWNING NodeType's assembly (the type the
/// root-registry materialization produces).</summary>
internal static class QueryLayerModel
{
    public enum Confidence { Hoch, Mittel, Niedrig }

    public record BalanceEntry
    {
        public string Position { get; init; } = string.Empty;
        public string Year { get; init; } = string.Empty;
        public double Amount { get; init; }
        public Confidence? Konfidenz { get; init; }
    }
}

/// <summary>The SAME record shape as compiled into a CONSUMING NodeType's assembly (an
/// <c>@@</c>-included copy) — same short names, different CLR identity.</summary>
internal static class ConsumerModel
{
    public enum Confidence { Hoch, Mittel, Niedrig }

    public record BalanceEntry
    {
        public string Position { get; init; } = string.Empty;
        public string Year { get; init; } = string.Empty;
        public double Amount { get; init; }
        public Confidence? Konfidenz { get; init; }
    }

    public record RenamedEntry
    {
        public string Position { get; init; } = string.Empty;
        public string Year { get; init; } = string.Empty;
    }
}
