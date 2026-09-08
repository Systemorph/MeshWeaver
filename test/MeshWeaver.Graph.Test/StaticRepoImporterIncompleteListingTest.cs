using System.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The prune's premise, not the prune's policy</b> (Systemorph/MeshWeaver#3589).
///
/// <para><b>What was measured.</b> On memex.systemorph.com the <c>Crm</c> partition had synced to the
/// built commit and still held <c>Crm/Source/Mail</c>, <c>Crm/Source/MailTests</c> and
/// <c>Crm/Source/MailView</c> — files the repository had retired. Because the #2813 source-fingerprint
/// gate hashes the LIVE source set, those three orphans made every <c>Crm</c> bundle refused forever
/// and every <c>Crm</c> page compile from source instead of adopting one.</para>
///
/// <para><b>What this file pins is the OPPOSITE-direction hazard on the same code path</b>, because the
/// two are one decision. Every other guard in
/// <see cref="StaticRepoImporter.ComputePrunableNodes"/> refines the inference "absent from the source
/// ⇒ deleted from the source". This one WITHDRAWS it: when the source could not enumerate itself
/// completely — GitHub answers HTTP 200 with a TRUNCATED recursive tree — an absent path is an unread
/// file, and subtracting the partition from a set that is not the whole source deletes live nodes on
/// the strength of a read that never happened.</para>
///
/// <para>Every assertion below is paired with its CONTROL: the identical inputs with a complete
/// listing, which must still prune. A guard that cannot be shown to change the answer is not a guard,
/// and this whole family of defects is "a boolean about something the code had to READ, collapsed
/// into a real answer".</para>
///
/// <para>Pure + deterministic (no database), mirroring <see cref="StaticRepoImporterSyncModeTest"/>.</para>
/// </summary>
public class StaticRepoImporterIncompleteListingTest
{
    private const string Partition = "Crm";

    /// <summary>The three retired source files #3589 found alive on the Crm partition.</summary>
    private static MeshNode[] Retired =>
    [
        new("Mail", $"{Partition}/Source") { State = MeshNodeState.Active },
        new("MailTests", $"{Partition}/Source") { State = MeshNodeState.Active },
        new("MailView", $"{Partition}/Source") { State = MeshNodeState.Active },
    ];

    /// <summary>The one file the repository still ships — the whole source listing when it is complete.</summary>
    private static readonly string[] CurrentSourcePaths = [$"{Partition}/Source/Api"];

    private static readonly string[] PreviousManifestPaths =
    [
        $"{Partition}/Source/Api", $"{Partition}/Source/Mail",
        $"{Partition}/Source/MailTests", $"{Partition}/Source/MailView",
    ];

    private static string[] Prune(PartitionSyncMode mode, bool listingIsComplete, params MeshNode[] existing) =>
        StaticRepoImporter.ComputePrunableNodes(
                existing, CurrentSourcePaths, PreviousManifestPaths, excludedRoots: [], mode,
                isExcludedFromMirror: null, listingIsComplete: listingIsComplete)
            .Select(n => n.Path)
            .ToArray();

    [Fact]
    public void AnIncompleteSourceListing_PrunesNothing_WhereACompleteOnePrunesEverythingAbsent()
    {
        // THE CONTROL FIRST, so a change that makes the guard's branch unreachable cannot pass this
        // test by returning empty for both. A complete listing is the shipped mirror behaviour.
        Prune(PartitionSyncMode.FullReplace, listingIsComplete: true, Retired)
            .Should().BeEquivalentTo(
                new[] { "Crm/Source/Mail", "Crm/Source/MailTests", "Crm/Source/MailView" },
                JsonSerializerOptions.Default,
                because: "a COMPLETE listing that no longer carries these files is evidence they were retired");

        // THE GUARD. Same nodes, same source paths, same mode — only the completeness of the read
        // differs, and it is the difference between a mirror and a data-loss event.
        Prune(PartitionSyncMode.FullReplace, listingIsComplete: false, Retired)
            .Should().BeEmpty(
                "an INCOMPLETE listing cannot support 'absent ⇒ deleted': GitHub returns HTTP 200 with "
                + "a truncated tree, so every file it happened not to return would read as a deletion "
                + "and a FullReplace import would mirror the whole Space away");
    }

    [Fact]
    public void AnIncompleteSourceListing_BeatsEveryMode_NotJustFullReplace()
    {
        // Additive already narrows to what the source PREVIOUSLY owned — and every retired file here
        // IS in the previous manifest, so Additive prunes them too. The completeness guard has to sit
        // AHEAD of the mode, not inside one of its branches.
        Prune(PartitionSyncMode.Additive, listingIsComplete: true, Retired)
            .Should().HaveCount(3, "these three are all in the previous manifest — the control must prune");
        Prune(PartitionSyncMode.Additive, listingIsComplete: false, Retired)
            .Should().BeEmpty("the premise is missing in Additive exactly as it is in FullReplace");

        // UpsertOnly never prunes anyway; asserted so a future reordering that makes the completeness
        // check unreachable behind the mode check is still visible here.
        Prune(PartitionSyncMode.UpsertOnly, listingIsComplete: false, Retired).Should().BeEmpty();
    }

    [Fact]
    public void ACompleteListingIsTheDEFAULT_SoNoExistingCallerChangesBehaviour()
    {
        // Every source that materializes from an embedded assembly or a full clone cannot return a
        // partial listing, and none of them says anything: the parameter defaults to true and the
        // interface member defaults to true. This pins that the fix is opt-IN to the refusal, so a
        // source that never declares anything keeps mirroring exactly as before.
        StaticRepoImporter.ComputePrunableNodes(
                Retired, CurrentSourcePaths, PreviousManifestPaths, excludedRoots: [],
                PartitionSyncMode.FullReplace)
            .Select(n => n.Path)
            .Should().HaveCount(3,
                "omitting the argument must mean 'complete', or every unmigrated source silently "
                + "stops pruning and the mesh fills up with retired files — which is the #3589 symptom");
    }

    [Fact]
    public void TheInterfaceDefault_IsAComplete_Listing()
    {
        // The default interface implementation is what silences the `Interface additions
        // (implementers declared)` gate AND what keeps every existing implementer — in this repo, in
        // MeshWeaver.Plugins, and in the in-mesh C# no compiler here can see — compiling untouched.
        // Read through the INTERFACE deliberately: a default interface member is not visible on the
        // concrete type, which is the same fact that makes it invisible to an implementer — and is
        // why supplying one is the sanctioned way to add to a public interface without obliging
        // anybody to change.
        IStaticRepoSource silent = new SilentSource();
        silent.ListingIsComplete.Should().BeTrue();
    }

    /// <summary>A source that declares nothing beyond the required members — i.e. every source that
    /// existed before #3589. It must read as COMPLETE.</summary>
    private sealed class SilentSource : IStaticRepoSource
    {
        public string Partition => "Silent";
        public bool Versioned => false;
        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => [];
    }

    // ── The other half of #3589: DID THE RUN CONVERGE? ───────────────────────────────────────────
    //
    // The refusal above is the prune's premise. This is what the run has to RECORD about itself, and
    // it is the half that decides whether the next run ever reaches the prune at all. The
    // content-addressed marker at {Partition}/_Activity/import-{fingerprint} is read INSTEAD of the
    // partition, so a run that left the partition unequal to the content that id names must not
    // stamp it as a licence to skip. Four ways to leave it unequal — each one pinned below.

    private static StaticRepoImportResult Result(
        string outcome = "Imported", int preserved = 0, int failed = 0, bool pruneRefused = false) =>
        new(Partition, "fp", outcome, Count: 1, Preserved: preserved)
        {
            Failed = failed,
            PruneRefused = pruneRefused,
        };

    [Fact]
    public void ACleanImport_Converged()
        => Result().Converged.Should().BeTrue(
            "nothing was kept back, nothing failed, and the prune was able to look — the partition "
            + "now holds exactly the content the marker's id names, which is the only state that "
            + "licenses the next run to skip reading it");

    [Fact]
    public void AnImportThatKeptNodesBack_DidNotConverge()
        => Result(preserved: 5).Converged.Should().BeFalse(
            "measured on memex.systemorph.com: the 2026-09-07 10:15Z Crm import kept back all five "
            + "nodes the repository had deleted (\"kept 5 local change(s), pruned 0\") and stamped "
            + "its marker Succeeded anyway. At 23:11Z the next sync read that marker, answered "
            + "Skipped without reading the partition, and its unmeasured Preserved = 0 advanced "
            + "LastSyncCommitSha to the branch head");

    [Fact]
    public void AnImportWhoseNodesDidNotAllLand_DidNotConverge()
        => Result(outcome: "ImportedWithErrors", failed: 2).Converged.Should().BeFalse(
            "a node that did not land is a node the partition does not hold — the same claim, and "
            + "the same reason the sync baseline is held (#2229 item C)");

    [Fact]
    public void AnImportWhosePruneWasRefused_DidNotConverge()
        => Result(pruneRefused: true).Converged.Should().BeFalse(
            "🚨 the case the count alone cannot see: preserved is 0 and pruned is 0, but the prune "
            + "never ran — the source listing came back truncated. \"Looked and found nothing\" and "
            + "\"could not look\" read identically in every log, and only the second means the "
            + "retired files are still there");

    [Fact]
    public void AFailedImport_DidNotConverge()
        => Result(outcome: "Failed").Converged.Should().BeFalse(
            "the whole import failed; it carries no per-file tally to read, so the outcome literal "
            + "is still the signal for that one");
}
