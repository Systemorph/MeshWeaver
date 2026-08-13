using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins the diagnostic for content PostgreSQL's <c>jsonb</c> cannot represent (#1449).
///
/// <para>The production incident: a node carrying a literal U+0000 in its content died on
/// <c>content = $12::jsonb</c> with <c>22P05: unsupported Unicode escape sequence</c> and a DETAIL
/// the connection policy redacts — naming neither the node, nor the field, nor the character. It is
/// not a size limit, not an index-row limit and not a narrow column: no widening makes U+0000
/// storable, because jsonb holds DECODED text and PostgreSQL text cannot contain a NUL byte.</para>
///
/// <para>So the contract these tests pin is: it fails, it fails BEFORE anything commits, and it says
/// exactly which node and which property to fix. Nothing is truncated or rewritten to make it fit.</para>
/// </summary>
[Collection("PostgreSql")]
public class UnstorableContentTests(PostgreSqlFixture fixture)
{
    private readonly JsonSerializerOptions _options = new();

    /// <summary>The character under test, taken from the production constant — never typed literally.</summary>
    private const char Nul = UnstorableContentException.Nul;

    /// <summary>U+001F UNIT SEPARATOR — the separator a composite key should use INSTEAD of U+0000.</summary>
    private const char UnitSeparator = (char)0x1F;

    [Fact]
    public async Task WriteRefusesNulContentAndNamesTheNodeAndProperty()
    {
        await fixture.CleanData().Should().Within(60.Seconds()).Emit();

        var node = new MeshNode("Poisoned", "ns")
        {
            Content = new Dictionary<string, object> { ["code"] = $"before{Nul}after" }
        };

        var ex = await Assert.ThrowsAsync<UnstorableContentException>(
            () => fixture.StorageAdapter.Write(node, _options).FirstAsync().ToTask());

        ex.NodePath.Should().Be("ns/Poisoned");
        ex.ContentProperty.Should().Be("code");
        ex.OccurrenceCount.Should().Be(1);
        // The message has to carry what the redacted 22P05 never did: the node, the field, the cause.
        ex.Message.Should().Contain("ns/Poisoned").And.Contain("code").And.Contain("22P05");

        // …and nothing landed.
        var stored = await fixture.StorageAdapter.Read("ns/Poisoned", _options)
            .Should().Within(30.Seconds()).Emit();
        stored.Should().BeNull();
    }

    [Fact]
    public async Task NulNestedInAnArrayIsLocatedByIndexedPath()
    {
        await fixture.CleanData().Should().Within(60.Seconds()).Emit();

        var node = new MeshNode("Deck", "ns")
        {
            Content = new Dictionary<string, object>
            {
                ["title"] = "clean",
                ["slides"] = new[] { "ok", $"bad{Nul}" }
            }
        };

        var ex = await Assert.ThrowsAsync<UnstorableContentException>(
            () => fixture.StorageAdapter.Write(node, _options).FirstAsync().ToTask());

        ex.ContentProperty.Should().Be("slides[1]");
    }

    /// <summary>
    /// The batch path is the one that used to lose the most: <c>WriteMany</c> windows its statements,
    /// so a poisoned node could fail its window AFTER an earlier window had already committed, with a
    /// redacted 22P05 naming none of the batch's members. The precondition sits in
    /// <c>BuildUpsertAsync</c> — which <c>WriteMany</c> runs for every node BEFORE executing any
    /// window — so the batch now fails whole, naming the one node responsible.
    /// </summary>
    [Fact]
    public async Task BatchWriteNamesThePoisonedNodeAndCommitsNothing()
    {
        await fixture.CleanData().Should().Within(60.Seconds()).Emit();

        MeshNode[] batch =
        [
            new MeshNode("Clean1", "ns") { Name = "One" },
            new MeshNode("Poisoned", "ns")
            {
                Content = new Dictionary<string, object> { ["body"] = $"x{Nul}y" }
            },
            new MeshNode("Clean2", "ns") { Name = "Two" }
        ];

        var ex = await Assert.ThrowsAsync<UnstorableContentException>(
            () => fixture.StorageAdapter.WriteMany(batch, _options).FirstAsync().ToTask());

        ex.NodePath.Should().Be("ns/Poisoned");

        foreach (var path in new[] { "ns/Clean1", "ns/Poisoned", "ns/Clean2" })
        {
            var stored = await fixture.StorageAdapter.Read(path, _options)
                .Should().Within(30.Seconds()).Emit();
            stored.Should().BeNull($"the batch was refused whole, so {path} must not exist");
        }
    }

    /// <summary>
    /// The guard is narrow ON PURPOSE: only U+0000 is unstorable. U+001F — the separator
    /// <c>DocumentAreaResolution</c> now uses in place of the literal NUL it used to carry — has the
    /// same "cannot appear in a path" property AND survives a jsonb round-trip. If this ever fails,
    /// the replacement separator is as unstorable as the character it replaced.
    /// </summary>
    [Fact]
    public async Task TheUnitSeparatorReplacementIsStorableAndRoundTrips()
    {
        await fixture.CleanData().Should().Within(60.Seconds()).Emit();

        var key = $"ClientDelta/Deck{UnitSeparator}area:Present";
        var node = new MeshNode("Keyed", "ns")
        {
            Content = new Dictionary<string, object> { ["key"] = key }
        };

        await fixture.StorageAdapter.Write(node, _options).Should().Within(30.Seconds()).Emit();

        var stored = await fixture.StorageAdapter.Read("ns/Keyed", _options)
            .Should().Within(30.Seconds()).Emit();
        stored.Should().NotBeNull();

        // Compare the DECODED value: the serialized form legitimately carries U+001F as an escape,
        // so asserting on the JSON text would test the encoder, not the round-trip.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(stored!.Content, _options));
        doc.RootElement.GetProperty("key").GetString().Should().Be(key,
            "U+001F must survive jsonb byte-exact — it is the separator chosen to replace U+0000");
    }

    [Fact]
    public async Task CleanContentIsUnaffectedByTheGuard()
    {
        await fixture.CleanData().Should().Within(60.Seconds()).Emit();

        var node = new MeshNode("Clean", "ns")
        {
            Content = new Dictionary<string, object> { ["status"] = "Open" }
        };

        await fixture.StorageAdapter.Write(node, _options).Should().Within(30.Seconds()).Emit();
        var stored = await fixture.StorageAdapter.Read("ns/Clean", _options)
            .Should().Within(30.Seconds()).Emit();
        stored.Should().NotBeNull();
    }
}
