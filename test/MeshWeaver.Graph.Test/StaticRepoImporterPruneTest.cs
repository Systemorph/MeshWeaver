using System;
using System.Text.Json;
using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for the source-owned partition prune DECISION
/// (<see cref="StaticRepoImporter.ComputeOrphanedSourcePartitions"/>) — the "removing partitions"
/// logic. A partition is pruned only when it was previously owned by a compiled static-repo source
/// (recorded by a marker) AND is no longer backed by a registered source this run. Adding a source,
/// or keeping one, never prunes; user/GitSync partitions are never in <c>previouslyOwned</c> so they
/// can never appear here. Pure + deterministic — no database.
/// </summary>
public class StaticRepoImporterPruneTest
{
    [Fact]
    public void RemovingSource_PreviouslyOwnedButGone_IsOrphaned()
    {
        // `command` was a source-owned catalog; it's no longer a registered source (unified into Skill).
        var orphans = StaticRepoImporter.ComputeOrphanedSourcePartitions(
            previouslyOwned: new[] { "Doc", "Agent", "Skill", "Command" },
            currentSources: new[] { "Doc", "Agent", "Skill" });

        orphans.Should().BeEquivalentTo(new[] { "Command" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void AddingSource_NewSourceNotPreviouslyOwned_IsNotOrphaned()
    {
        // A brand-new catalog (Skill) added this run — present in current sources, so never pruned.
        var orphans = StaticRepoImporter.ComputeOrphanedSourcePartitions(
            previouslyOwned: new[] { "Doc", "Agent" },
            currentSources: new[] { "Doc", "Agent", "Skill" });

        orphans.Should().BeEmpty();
    }

    [Fact]
    public void KeepingSources_AllStillRegistered_NoOrphans()
    {
        var orphans = StaticRepoImporter.ComputeOrphanedSourcePartitions(
            previouslyOwned: new[] { "Doc", "Agent", "Skill" },
            currentSources: new[] { "Doc", "Agent", "Skill" });

        orphans.Should().BeEmpty();
    }

    [Fact]
    public void Comparison_IsCaseInsensitive()
    {
        // Marker casing must not matter — schema names are lowercased, namespaces are not.
        var orphans = StaticRepoImporter.ComputeOrphanedSourcePartitions(
            previouslyOwned: new[] { "agent", "COMMAND" },
            currentSources: new[] { "Agent" });

        orphans.Should().BeEquivalentTo(new[] { "COMMAND" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void NothingPreviouslyOwned_NoOrphans()
    {
        StaticRepoImporter.ComputeOrphanedSourcePartitions(
            Array.Empty<string>(), new[] { "Doc", "Agent" })
            .Should().BeEmpty();
    }

    [Fact]
    public void NoCurrentSources_AllPreviouslyOwnedAreOrphaned()
    {
        // Defensive: if a deployment registered NO sources, it must not interpret that as "prune
        // everything" silently — the caller (ImportAll) returns early when sources.Length == 0, so this
        // function is never invoked with an empty current set in practice. Documented here as the
        // raw-function contract (it WOULD orphan all) so the early-return guard stays load-bearing.
        var orphans = StaticRepoImporter.ComputeOrphanedSourcePartitions(
            previouslyOwned: new[] { "Doc", "Agent" },
            currentSources: Array.Empty<string>());

        orphans.Should().BeEquivalentTo(new[] { "Doc", "Agent" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void EmptyAndNullEntries_AreIgnored()
    {
        var orphans = StaticRepoImporter.ComputeOrphanedSourcePartitions(
            previouslyOwned: new[] { "", "Command", null! },
            currentSources: new[] { "Doc", "" });

        orphans.Should().BeEquivalentTo(new[] { "Command" }, JsonSerializerOptions.Default);
    }
}
