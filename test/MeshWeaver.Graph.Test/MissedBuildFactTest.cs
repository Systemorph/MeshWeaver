using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The record that exists to make a SILENT loss visible must not itself be readable only through a
/// query that is silently blind — Systemorph/MeshWeaver#3374.
///
/// <para><c>MissedBuildFact</c> lands in the <b>Admin partition</b>, exactly like
/// <see cref="BuildCompletion"/>, and an UNSCOPED query does not reach that partition: a bare
/// <c>nodeType:MissedBuildFact</c> answers EMPTY however many records exist. That failure mode is
/// the reason this test is worth writing rather than obvious — "no build facts have been missed"
/// and "the query cannot see them" would be the same answer, on the one record whose entire job is
/// to stop a loss from looking like a success. <see cref="BuildCompletionWatchQueryTest"/> records
/// what that cost when the self-updater hit it: the unscoped form answered 0 while
/// <c>Admin/_Build/Systemorph.MeshWeaver</c> sat at version 1746.</para>
/// </summary>
public class MissedBuildFactTest
{
    [Fact]
    public void TheWatchQuery_IsPathScoped_BecauseTheAdminPartitionIsInvisibleUnscoped()
    {
        Assert.Contains($"path:{MissedBuildFact.Namespace}", MissedBuildFact.WatchQuery);
        Assert.Contains("scope:children", MissedBuildFact.WatchQuery);
        Assert.Contains($"nodeType:{MissedBuildFact.NodeType}", MissedBuildFact.WatchQuery);

        Assert.Equal(
            "path:Admin/_MissedBuild scope:children nodeType:MissedBuildFact",
            MissedBuildFact.WatchQuery);
    }

    [Fact]
    public void TheRecordPairsWithTheBuildRecordItStandsIn_For()
    {
        // Same {owner}.{repo} id under a sibling namespace, so a reader holding one path can derive
        // the other without a lookup table — which is what IsSupersededBy's caller has to do.
        var missed = MissedBuildFact.PathFor("Systemorph", "MeshWeaver");
        var build = BuildCompletion.PathFor("Systemorph", "MeshWeaver");

        Assert.Equal("Admin/_MissedBuild/Systemorph.MeshWeaver", missed);
        Assert.StartsWith(MissedBuildFact.Namespace + "/", missed);
        Assert.Equal(
            build[(build.LastIndexOf('/') + 1)..],
            missed[(missed.LastIndexOf('/') + 1)..]);
    }

    [Fact]
    public void APayloadOwnerCannotFabricateNodeHierarchy()
    {
        // owner/repo arrive from a webhook body. GitHub names cannot contain a separator, but the
        // value is untrusted, and a surviving '/' would write the record somewhere else entirely —
        // out of the namespace the watch query scopes to, i.e. invisible again.
        var path = MissedBuildFact.PathFor("evil/owner", @"re\po");

        Assert.Equal("Admin/_MissedBuild/evil-owner.re-po", path);
        Assert.StartsWith(MissedBuildFact.Namespace + "/", path);
    }

    /// <summary>
    /// A repository with no build record at all cannot have superseded anything — the case that
    /// matters, because it is the state right after the very failure this record documents.
    /// </summary>
    [Fact]
    public void NoCurrentBuildRecord_SupersedesNothing()
        => Assert.False(Missed(runNumber: 10).IsSupersededBy(null));

    [Theory]
    [InlineData(11, true)]   // a later run recorded the fact and ran the sync — this is history now
    [InlineData(10, true)]   // the SAME run, recorded on a retry: exactly what was being held
    [InlineData(9, false)]   // an older run says nothing about the gap
    public void ASameWorkflowRun_SupersedesOnlyFromTheMissedRunOnward(long current, bool expected)
        => Assert.Equal(expected, Missed(runNumber: 10).IsSupersededBy(Build("CI", current)));

    /// <summary>
    /// 🚨 The guard that stops a real gap being retired by an unrelated workflow. Run numbers are
    /// per-workflow counters, so a busy second workflow on the same repository will routinely sit
    /// far ahead — and comparing across workflows would mark every miss superseded almost
    /// immediately, turning this record back into the silent loss it replaced.
    /// </summary>
    [Fact]
    public void ADifferentWorkflowsHigherRunNumber_DoesNotSupersede()
        => Assert.False(Missed(runNumber: 10).IsSupersededBy(Build("Some Other Workflow", 9999)));

    private static MissedBuildFact Missed(long runNumber) =>
        MissedBuildFact.For(Build("CI", runNumber), "owner returned no verdict", DateTimeOffset.UtcNow);

    private static BuildCompletion Build(string workflow, long runNumber) => new()
    {
        RepositoryUrl = "https://github.com/Systemorph/MeshWeaver",
        Branch = "main",
        HeadSha = "deadbeef",
        WorkflowName = workflow,
        RunNumber = runNumber,
    };
}
