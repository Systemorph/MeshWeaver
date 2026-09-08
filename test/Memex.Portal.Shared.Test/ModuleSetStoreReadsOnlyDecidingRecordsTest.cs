using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// <see cref="ModuleSetStore.Read"/> decides from the record NAMES and opens only the records the
/// decision needs. Measured on memex-cloud, 2026-09-08: 687 set records under <c>modules/sets</c>
/// on Azure Files, every one read on every call, 10 s per call — inside a health check that the
/// startup probe asks every 10 s with a 5 s timeout. No new pod could pass the probe, so the 8059
/// rollout sat for hours on two ageing replicas and KEDA could add nothing.
///
/// <para>Each test here plants records that the OLD reader would have opened and the new one must
/// not: a corrupt superseded proposal is the falsifier — reading it reports it, and the assertion
/// is that nothing is reported. The records are written in the store's own on-disk shape
/// (<c>&lt;sequence:D9&gt;-&lt;id16&gt;.proposed.json</c>, web-cased JSON) so the reader is measured
/// against the format it will meet on a volume, not against a mock.</para>
/// </summary>
public sealed class ModuleSetStoreReadsOnlyDecidingRecordsTest : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-setstore-read-" + Guid.NewGuid().ToString("N"));

    public ModuleSetStoreReadsOnlyDecidingRecordsTest()
    {
        Directory.CreateDirectory(ModuleSetStore.SetsDirectory(root));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    private static ModuleSet Set(long sequence, string idSeed, string generation = "g")
        => new(
            sequence,
            Id: Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(idSeed))),
            Generations: ImmutableSortedDictionary<string, string>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase)
                .Add("MeshWeaver.Speech", generation + sequence.ToString("D4")),
            ProposedAtUtc: new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc).AddSeconds(sequence),
            ProposedBy: "replica-" + idSeed);

    /// <summary>Writes a proposal exactly where and how the store writes one.</summary>
    private string WriteProposal(ModuleSet set, string? fileNameOverride = null)
    {
        var name = fileNameOverride ?? (set.Sequence.ToString("D9") + "-" + set.Id[..16] + ".proposed.json");
        var path = Path.Combine(ModuleSetStore.SetsDirectory(root), name);
        File.WriteAllText(path, JsonSerializer.Serialize(set, Web));
        return path;
    }

    private static void Corrupt(string path) => File.WriteAllText(path, "{");

    [Fact]
    public void ACorruptSupersededRecordIsNeverOpened_SoItIsNeverReported()
    {
        // 300 sequences, one proposal each; the one before the newest is the adopted (current) set.
        var files = new Dictionary<long, string>();
        for (var sequence = 1L; sequence <= 300; sequence++)
            files[sequence] = WriteProposal(Set(sequence, "seed-" + sequence));
        var adopted = Set(299, "seed-299");
        ModuleSetStore.RecordAdoption(root, adopted, adopted.Generations, "replica-x");

        // Every record no decision depends on is made unreadable. The old reader opened all of
        // them and reported each; this reader must not touch a single one.
        foreach (var (sequence, path) in files)
            if (sequence < 299)
                Corrupt(path);

        var reported = new List<string>();
        var index = ModuleSetStore.Read(root, reported.Add);

        Assert.Empty(reported);
        Assert.Equal(300, index.Proposed!.Sequence);
        Assert.Equal(299, index.Current!.Sequence);
        Assert.Equal(adopted.Id, index.Current.Id);
        Assert.Equal("g0299", index.RunningGenerations["meshweaver.speech"]);
        Assert.True(index.ConvergencePending);
    }

    [Fact]
    public void ConflictsAreReportedOnlyForTheSequencesDecidedOn()
    {
        // Two replicas proposed sequence 100 (history) and two proposed sequence 300 (the newest).
        WriteProposal(Set(100, "a-100"));
        WriteProposal(Set(100, "b-100"));
        for (var sequence = 101L; sequence < 300; sequence++)
            WriteProposal(Set(sequence, "seed-" + sequence));
        var alpha = Set(300, "a-300");
        var beta = Set(300, "b-300");
        WriteProposal(alpha);
        WriteProposal(beta);

        var reported = new List<string>();
        var index = ModuleSetStore.Read(root, reported.Add);

        Assert.Equal([300L], index.ConflictingSequences);
        var winner = string.CompareOrdinal(alpha.Id, beta.Id) < 0 ? alpha : beta;
        Assert.Equal(winner.Id, index.Proposed!.Id);
        var notice = Assert.Single(reported);
        Assert.Contains("sequence 300", notice);
        Assert.DoesNotContain("sequence 100", notice);
    }

    [Fact]
    public void ANewestSequenceThatCannotBeRead_FallsToTheNextReadableOne_AndReportsExactlyThat()
    {
        for (var sequence = 1L; sequence <= 299; sequence++)
            WriteProposal(Set(sequence, "seed-" + sequence));
        var broken = WriteProposal(Set(300, "seed-300"));
        Corrupt(broken);

        var reported = new List<string>();
        var index = ModuleSetStore.Read(root, reported.Add);

        Assert.Equal(299, index.Proposed!.Sequence);
        var fault = Assert.Single(reported);
        Assert.Contains(Path.GetFileName(broken), fault);
    }

    [Fact]
    public void ACorruptAdoptionFallsThroughToTheNextAdoptedSequence()
    {
        for (var sequence = 1L; sequence <= 300; sequence++)
            WriteProposal(Set(sequence, "seed-" + sequence));
        var older = Set(250, "seed-250");
        var newer = Set(299, "seed-299");
        ModuleSetStore.RecordAdoption(root, older, older.Generations, "replica-x");
        ModuleSetStore.RecordAdoption(root, newer, newer.Generations, "replica-y");
        var newerAdoption = Directory
            .EnumerateFiles(ModuleSetStore.SetsDirectory(root), "000000299-*.adopted.json")
            .Single();
        Corrupt(newerAdoption);

        var reported = new List<string>();
        var index = ModuleSetStore.Read(root, reported.Add);

        Assert.Equal(300, index.Proposed!.Sequence);
        Assert.Equal(250, index.Current!.Sequence);
        Assert.Equal("g0250", index.RunningGenerations["MeshWeaver.Speech"]);
        var fault = Assert.Single(reported);
        Assert.Contains(Path.GetFileName(newerAdoption), fault);
    }

    [Fact]
    public void ARecordUnderAForeignName_IsStillRead()
    {
        for (var sequence = 1L; sequence <= 10; sequence++)
            WriteProposal(Set(sequence, "seed-" + sequence));
        // Not a name this store ever writes — placed by hand, it still counts, by its content.
        WriteProposal(Set(400, "hand-placed"), fileNameOverride: "hand-placed.proposed.json");

        var index = ModuleSetStore.Read(root);

        Assert.Equal(400, index.Proposed!.Sequence);
    }

    [Fact]
    public void AnEmptyStore_ReadsAsEmpty()
    {
        Assert.Same(ModuleSetIndex.Empty, ModuleSetStore.Read(root));
    }
}
