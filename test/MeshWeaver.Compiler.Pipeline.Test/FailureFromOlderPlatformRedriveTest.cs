using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

using MeshWeaver.Compiler;
namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A NodeType whose last compile FAILED on an OLDER platform build than the one now running is
/// retried ONCE by the newer build — and never by the build it failed on, nor by an older one.
///
/// <para><b>The incident.</b> On the control instance (2026-10-09) <c>Hosting/ApprovalInbox</c> and
/// <c>Essentials/OperationRequest</c> failed on 3.0.0-ci.10300 with <c>CS0246 ClickProgress</c> /
/// <c>CS1061 TrackNode</c> — framework APIs the next build added. The replica on that next build
/// never retried them: the failure token's <c>fw=</c> is the platform COMPATIBILITY KEY
/// (<c>c003e001</c>), identical on both builds, and the token re-drive excludes every type with a
/// last good build anyway. So both kept serving their morning build until a person pressed
/// Compile.</para>
///
/// <para>These are the pure pins of <see cref="NodeTypeCompilationHelpers.HasFailureFromOlderPlatform"/>
/// and of the commit step that applies it (<see cref="NodeTypeCompilationHelpers.ApplyFailedVerdictRedrive"/>):
/// the positive case, the same-build NEGATIVE CONTROL, the older-replica case that must not
/// ping-pong, and the stamps that keep it bounded.</para>
/// </summary>
public class FailureFromOlderPlatformRedriveTest
{
    private const string TypePath = "Hosting/ApprovalInbox";
    private const string ModulesHash = "mod-1";
    private const string OldBuild = "3.0.0-ci.10300";
    private const string NewBuild = "3.0.0-ci.10310";

    private static readonly ImmutableDictionary<string, long> LiveSources =
        ImmutableDictionary<string, long>.Empty
            .Add("Hosting/ApprovalInbox/Source/ApprovalInboxRows", 5)
            .Add("Hosting/ApprovalInbox/Source/ApprovalInboxFeed", 6);

    /// <summary>The incident's shape: Error on the old build, still holding a LAST GOOD build (so the
    /// token re-drive cannot see it), the token formed under exactly the live inputs (so nothing in
    /// the token moved either).</summary>
    private static NodeTypeDefinition FailedOn(string? failedBuild) => new()
    {
        Configuration = "config => config",
        Sources = ["namespace:Source scope:subtree"],
        CompilationStatus = CompilationStatus.Error,
        CompilationError = "CS0246: The type or namespace name 'ClickProgress' could not be found",
        CurrentSourceVersions = LiveSources,
        LastCompileSucceededAt = System.DateTimeOffset.Parse("2026-10-09T08:25:13Z"),
        LatestAssemblyCollection = "local",
        LatestAssemblyPath = "Hosting_ApprovalInbox/v30-c003e001-348e7eb523d9.dll",
        CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
        CompiledPlatformVersion = OldBuild,
        FailedBuildInputs = NodeTypeCompilationHelpers.BuildInputsToken(ModulesHash, LiveSources),
        FailedPlatformVersion = failedBuild,
    };

    private static MeshNode Node(NodeTypeDefinition def) =>
        new("ApprovalInbox", "Hosting") { NodeType = MeshNode.NodeTypePath, Content = def };

    [Fact]
    public void AFailureFromAnOlderBuild_IsRetriedOnce_OnTheNewerBuild()
    {
        var def = FailedOn(OldBuild);

        // The two triggers that existed before this fix are both blind to it — the reason for it.
        NodeTypeCompilationHelpers.HasStaleFailureVerdict(def, ModulesHash, TypePath)
            .Should().BeFalse("the token is identical across two builds of one compatibility key");

        NodeTypeCompilationHelpers.HasFailureFromOlderPlatform(def, NewBuild, TypePath)
            .Should().BeTrue("the newer build may well ship the API the failing source needed");

        var redriven = (NodeTypeDefinition)NodeTypeCompilationHelpers.ApplyFailedVerdictRedrive(
            Node(def), TypePath, ModulesHash, parkRegistry: null, livePlatformVersion: NewBuild).Content!;

        redriven.CompilationStatus.Should().Be(CompilationStatus.Pending, "the retry is dispatched");
        redriven.FailedPlatformVersion.Should().Be(NewBuild,
            "the build this attempt is made on is stamped in the SAME write as the flip");
        redriven.LatestAssemblyPath.Should().Be(def.LatestAssemblyPath,
            "the last good build keeps serving until the retry succeeds");

        // Bounded: once the attempt on this build has failed, this build never retries it again.
        var failedAgain = redriven with { CompilationStatus = CompilationStatus.Error };
        NodeTypeCompilationHelpers.HasFailureFromOlderPlatform(failedAgain, NewBuild, TypePath)
            .Should().BeFalse("one attempt per build — never a loop on a same-build failure");
    }

    [Fact]
    public void TheRetry_ThatSucceeds_ClearsTheStamp()
    {
        var pending = (NodeTypeDefinition)NodeTypeCompilationHelpers.ApplyFailedVerdictRedrive(
            Node(FailedOn(OldBuild)), TypePath, ModulesHash, parkRegistry: null,
            livePlatformVersion: NewBuild).Content!;

        var ok = NodeTypeCompilationHelpers.ApplyCompileSuccess(
            pending with { CompilationStatus = CompilationStatus.Compiling },
            new NodeCompilationResult(
                AssemblyLocation: "/cache/ApprovalInbox/T.dll",
                NodeTypeConfigurations: [],
                CompiledSources: LiveSources,
                Collection: "local",
                ContentPath: "Hosting_ApprovalInbox/v46.dll",
                Version: 46),
            currentNodeVersion: 46, activityPath: null, releasePath: null, modulesHash: ModulesHash);

        ok.CompilationStatus.Should().Be(CompilationStatus.Ok);
        ok.FailedPlatformVersion.Should().BeNull(
            "a success retires the failure verdict, and the build it was formed on goes with it");
        NodeTypeCompilationHelpers.HasFailureFromOlderPlatform(ok, NewBuild, TypePath).Should().BeFalse();
    }

    /// <summary>NEGATIVE CONTROL: the identical record, failed on the build that is running — the
    /// predicate and the commit step both decline, and the record is returned untouched.</summary>
    [Fact]
    public void AFailureFromTheSameBuild_IsNotRetried()
    {
        var def = FailedOn(NewBuild);

        NodeTypeCompilationHelpers.HasFailureFromOlderPlatform(def, NewBuild, TypePath)
            .Should().BeFalse("a verdict formed on this very build is settled");

        var node = Node(def);
        NodeTypeCompilationHelpers.ApplyFailedVerdictRedrive(
                node, TypePath, ModulesHash, parkRegistry: null, livePlatformVersion: NewBuild)
            .Should().BeSameAs(node, "nothing may be written for a settled same-build failure");
    }

    /// <summary>During a roll an OLDER replica must not retry a failure the newer build formed —
    /// otherwise two images ping-pong one type between them.</summary>
    [Fact]
    public void AFailureFromANewerBuild_IsNotRetriedByAnOlderReplica()
    {
        NodeTypeCompilationHelpers.HasFailureFromOlderPlatform(FailedOn(NewBuild), OldBuild, TypePath)
            .Should().BeFalse();
    }

    /// <summary>Every failure recorded before the field existed: one attempt, then stamped.</summary>
    [Fact]
    public void AnUnstampedFailure_IsRetriedOnce()
    {
        var def = FailedOn(failedBuild: null);
        NodeTypeCompilationHelpers.HasFailureFromOlderPlatform(def, NewBuild, TypePath).Should().BeTrue();

        var redriven = (NodeTypeDefinition)NodeTypeCompilationHelpers.ApplyFailedVerdictRedrive(
            Node(def), TypePath, ModulesHash, parkRegistry: null, livePlatformVersion: NewBuild).Content!;
        NodeTypeCompilationHelpers.HasFailureFromOlderPlatform(
                redriven with { CompilationStatus = CompilationStatus.Error }, NewBuild, TypePath)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3.0.0-ci.0")] // a local build is unordered against every CI build
    public void AnUnknownOrUnorderedLiveBuild_NeverRetries(string? live)
    {
        NodeTypeCompilationHelpers.HasFailureFromOlderPlatform(FailedOn(OldBuild), live, TypePath)
            .Should().BeFalse();

        var node = Node(FailedOn(OldBuild));
        NodeTypeCompilationHelpers.ApplyFailedVerdictRedrive(
                node, TypePath, ModulesHash, parkRegistry: null, livePlatformVersion: live)
            .Should().BeSameAs(node);
    }

    [Theory]
    [InlineData(CompilationStatus.Ok)]
    [InlineData(CompilationStatus.Pending)]
    [InlineData(CompilationStatus.Compiling)]
    public void OnlyASettledError_IsRetried(CompilationStatus status)
    {
        NodeTypeCompilationHelpers.HasFailureFromOlderPlatform(
                FailedOn(OldBuild) with { CompilationStatus = status }, NewBuild, TypePath)
            .Should().BeFalse();
    }

    /// <summary>The source set must be established before a compile is driven from it (#1216).</summary>
    [Fact]
    public void AnUnseededSourceSet_IsNotRetriedYet()
    {
        NodeTypeCompilationHelpers.HasFailureFromOlderPlatform(
                FailedOn(OldBuild) with { CurrentSourceVersions = null }, NewBuild, TypePath)
            .Should().BeFalse();
    }

    [Fact]
    public void TheStamp_IsOperationalState()
    {
        NodeTypeOperationalContent.MemberNames
            .Contains(nameof(NodeTypeDefinition.FailedPlatformVersion))
            .Should().BeTrue("an authored value naming a future build would suppress the retry");

        var state = NodeTypeCompileState.FromDefinition(new NodeTypeDefinition { FailedPlatformVersion = OldBuild });
        state!.FailedPlatformVersion.Should().Be(OldBuild);
        state.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void AGateRefusal_FormedUnderTheLiveInputs_StampsTheLiveBuild()
    {
        var settled = NodeTypeCompilationHelpers.ApplyGateSettle(
            FailedOn(OldBuild), "refused", formedUnderLiveInputs: true, ModulesHash);
        settled.FailedPlatformVersion.Should().Be(
            NodeTypeCompilationHelpers.LivePlatformVersion ?? OldBuild);

        var reServed = NodeTypeCompilationHelpers.ApplyGateSettle(
            FailedOn(OldBuild), reason: null, formedUnderLiveInputs: false, ModulesHash);
        reServed.FailedPlatformVersion.Should().Be(OldBuild,
            "re-serving a remembered verdict must not rewrite the build it was formed on");
    }
}
