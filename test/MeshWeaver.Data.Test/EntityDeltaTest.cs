using System.Text.Json;
using MeshWeaver.Data.Serialization;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins <see cref="EntityDelta"/> — the entity-level wiring that turns a full
/// <c>old → new</c> change into a compact <see cref="EntityDeltaUpdate"/> and
/// reconstructs the full entity by replaying the splice onto the owner's CURRENT
/// value. This is the core the cross-hub write transport will use so a big edited
/// string (markdown, prerendered html) syncs as a splice, not a full re-send.
/// </summary>
public class EntityDeltaTest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public record Inner(string Body, string Lang);
    public record Doc(string Id, Inner Content, string Title);

    [Fact]
    public void Compute_Then_Apply_ReproducesUpdated_AndShipsOnlyTheSplice()
    {
        var old = new Doc("1", new Inner("The quick brown fox", "en"), "T");
        var updated = new Doc("1", new Inner("The quick RED brown fox", "en"), "T2");

        var delta = EntityDelta.Compute("Doc", "1", partition: null, old, updated, Options);

        // The delta carries a splice, NOT the whole (unchanged) body.
        delta.Delta.Content.Should().Contain(StringDeltaPatch.Marker);
        delta.Delta.Content.Should().NotContain("The quick brown fox",
            "the unchanged body prefix must not travel — only the inserted fragment");

        // Owner replays onto its current (== old here) → reconstructs updated exactly.
        var reconstructed = (Doc)EntityDelta.Apply(old, delta, Options);
        reconstructed.Should().Be(updated);
    }

    [Fact]
    public void Apply_OntoDivergedOwner_MergesDisjointEdits()
    {
        var basis = new Doc("1", new Inner("The quick brown fox jumps", "en"), "T");
        var incoming = new Doc("1", new Inner("The VERY quick brown fox jumps", "en"), "T");
        var delta = EntityDelta.Compute("Doc", "1", null, basis, incoming, Options);

        // Owner already changed the END of the nested body (a disjoint concurrent edit).
        var ownerCurrent = new Doc("1", new Inner("The quick brown fox leaps", "en"), "T");
        var merged = (Doc)EntityDelta.Apply(ownerCurrent, delta, Options);

        merged.Content.Body.Should().Be("The VERY quick brown fox leaps",
            "the splice replays onto the owner's current text → disjoint edits merge, not clobber");
    }
}
