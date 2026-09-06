using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 A release re-cut is a RETRY of the release ONE settle owns — never a second cut
/// (Systemorph/MeshWeaver#3407).
///
/// <para><b>The defect.</b> <c>TryCreateReleaseNode</c> minted <c>{yyyyMMddHHmmss}-{contentHash}</c>
/// from <c>DateTime.UtcNow</c> inside its own body, so the id was a function of WHEN an attempt ran.
/// <c>ReleasePostCondition</c>'s re-cut — the same operation, the same bytes, one retry later —
/// therefore addressed a DIFFERENT node than the attempt it was retrying, and wall-clock luck picked
/// which of two wrong outcomes you got. A retry in a LATER second created a duplicate release for
/// one build (measured on memex 2026-09-06: <c>Hosting/InstanceAction/Release/20260906113905-cgZ9cItJ</c>
/// and <c>…113915-cgZ9cItJ</c>, byte-identical content, 10.005 s apart — the client-side bound on the
/// first attempt's observation). A retry in the SAME second minted the id its own first attempt had
/// already created: the create-only write was refused "Node already exists", the refusal was
/// swallowed to <c>null</c>, and <c>LatestReleasePath</c> was never advanced — the type left
/// advertising a build no release names, every instance still binding the previous assembly.</para>
///
/// <para><b>What this pins.</b> The invariant that removes both outcomes at once: given ONE
/// <see cref="NodeTypeBuildState.ReleaseIdentity"/>, every attempt at that release converges on ONE
/// node and reports ONE path. No timing is involved — the identity is a value the settle holds, so
/// the two attempts here are exactly as far apart as they need to be to prove it: not at all.</para>
///
/// <para>🚨 <b>A controlled experiment, not one observation.</b>
/// <see cref="ADifferentBuild_StillGetsItsOwnRelease"/> is the falsifying arm: an invariant that
/// merely stapled every attempt to one path would satisfy the first test just as well, and would
/// mean a NodeType could never publish a second release at all.</para>
/// </summary>
public class ReleaseRecutIsARetryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "TestData/RecutRelease";
    private const string ReleaseNamespace = TypePath + "/" + GraphNodeTypeNames.ReleaseSegment;

    /// <summary>The bytes a compile just produced — the coordinates the release id hashes.</summary>
    private static NodeCompilationResult Built(int version = 767) => new(
        AssemblyLocation: $"/cache/RecutRelease/v{version}/RecutRelease.dll",
        NodeTypeConfigurations: [],
        CompiledSources: ImmutableDictionary<string, long>.Empty.Add($"{TypePath}/Source/Model", 42),
        Collection: "assemblies",
        ContentPath: $"TestData_RecutRelease/v{version}.dll",
        Version: version);

    /// <summary>
    /// The NodeType exactly as the settle observed it at dispatch: a release request CONSUMED
    /// (<c>LastReleaseRequestHandledAt == RequestedReleaseAt</c>) and <c>LatestReleasePath</c> still
    /// naming the PREVIOUS build. That is the state <c>ReleasePostCondition.Violation</c> reports, so
    /// the re-cut below really is the remedy path and not a no-op.
    /// </summary>
    private static MeshNode Pending()
    {
        var requested = DateTimeOffset.UtcNow;
        return MeshNode.FromPath(TypePath) with
        {
            NodeType = MeshNode.NodeTypePath,
            Name = "RecutRelease",
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                RequestedReleaseAt = requested,
                LastReleaseRequestHandledAt = requested,
                LatestReleasePath = $"{ReleaseNamespace}/20260906094902-qc4SQmpO",
                LastCompiledVersion = 766,
            },
        };
    }

    [Fact(Timeout = 180_000)]
    public async Task ARecut_ConvergesOnTheReleaseItsOwnFirstAttemptWrote()
    {
        var result = Built();
        var pending = Pending();

        // The identity the SETTLE owns — minted once, exactly where the compile settle mints it.
        //
        // 🚨 Then re-stamped to an instant that is NOT "now", and that is what makes the contract
        // TESTABLE rather than merely likely. The settle mints when the compile settles; the
        // attempts run afterwards, so the stamp is never the attempt's own second. A write body
        // that re-derived the id from DateTime.UtcNow — the #3407 defect — would silently AGREE
        // with an identity minted in the same second, and every assertion below would pass while
        // the defect stood. Against a stamp of its own, disagreement is certain.
        var minted = NodeTypeBuildState.MintReleaseIdentity(result);
        var settledAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var identity = new NodeTypeBuildState.ReleaseIdentity(
            $"{settledAt.UtcDateTime:yyyyMMddHHmmss}-{minted.Hash}", minted.Hash, settledAt);
        var expected = $"{ReleaseNamespace}/{identity.Version}";

        // THE FIRST ATTEMPT. It writes the release node.
        var first = await NodeTypeBuildState
            .TryCreateReleaseNode(Mesh, TypePath, result, pending, activityPath: null, identity, logger: null)
            .Should().Within(60.Seconds())
            .Emit("the first attempt must report the path it wrote");
        first.Should().Be(expected,
            "the release identity is the SETTLE's value — the write addresses the node the caller "
            + "named, it does not re-derive an id of its own (#3407)");

        var afterFirst = await Mesh.GetMeshNodeStream(expected).Should().Within(60.Seconds())
            .Match(n => n is not null, "the write landed, so the node is readable");
        afterFirst.ContentAs<NodeTypeRelease>(Mesh.JsonSerializerOptions)!.AssemblyStoreVersion
            .Should().Be(result.Version, "this release names the build the compile just produced");

        // THE RE-CUT, run exactly as the settle runs it when the first attempt's OUTCOME was lost:
        // newReleasePath null, same bytes, same identity. On the defect this either minted a fresh
        // id (a duplicate release) or the SAME id (refused "already exists" → null → stale pointer).
        var (recutPath, diagnosis) = await ReleasePostCondition
            .Restore(Mesh, TypePath, result, pending, activityPath: null,
                newReleasePath: null, identity, logger: null)
            .Should().Within(60.Seconds())
            .Emit("Restore emits exactly once and never faults — the terminal Status write runs in "
                + "its OnNext");

        diagnosis.Should().NotBeNull(
            "the arrangement IS a post-condition violation, so the remedy path really ran — without "
            + "this the assertion below could pass on a re-cut that never happened");
        recutPath.Should().Be(expected,
            "a re-cut is a RETRY: it must converge on the node its own first attempt wrote, so "
            + "latestReleasePath advances to a release that EXISTS and names this build. A fresh "
            + "wall-clock id gives a duplicate release in a later second and, in the same second, "
            + "the collision that leaves the pointer on the previous build (#3407)");

        // THE END STATE — the type advertises a build a release actually names, and the adopt did
        // not touch the node: identical content, so the owner's no-op upsert guard recognises it.
        var afterRecut = await Mesh.GetMeshNodeStream(expected).Should().Within(60.Seconds())
            .Match(n => n is not null, "the release the pointer names still exists");
        afterRecut.Version.Should().Be(afterFirst.Version,
            "the re-cut wrote byte-identical content — CreatedAt is minted with the identity, not "
            + "per attempt — so adopting it must not bump the node's version");
    }

    [Fact(Timeout = 180_000)]
    public async Task ADifferentBuild_StillGetsItsOwnRelease()
    {
        var pending = Pending();

        var buildA = Built(801);
        var identityA = NodeTypeBuildState.MintReleaseIdentity(buildA);
        var buildB = Built(802);
        var identityB = NodeTypeBuildState.MintReleaseIdentity(buildB);

        identityB.Hash.Should().NotBe(identityA.Hash,
            "the id hashes the durable assembly coordinates, so two builds are two releases");

        var pathA = await NodeTypeBuildState
            .TryCreateReleaseNode(Mesh, TypePath, buildA, pending, activityPath: null, identityA, logger: null)
            .Should().Within(60.Seconds()).Emit();
        var pathB = await NodeTypeBuildState
            .TryCreateReleaseNode(Mesh, TypePath, buildB, pending, activityPath: null, identityB, logger: null)
            .Should().Within(60.Seconds()).Emit();

        pathB.Should().NotBe(pathA,
            "convergence is per IDENTITY, not per NodeType — a fix that stapled every attempt to one "
            + "path would satisfy the retry test above while making a second release impossible");

        var a = await Mesh.GetMeshNodeStream(pathA!).Should().Within(60.Seconds()).Match(n => n is not null);
        var b = await Mesh.GetMeshNodeStream(pathB!).Should().Within(60.Seconds()).Match(n => n is not null);
        a.ContentAs<NodeTypeRelease>(Mesh.JsonSerializerOptions)!.AssemblyStoreVersion.Should().Be(buildA.Version);
        b.ContentAs<NodeTypeRelease>(Mesh.JsonSerializerOptions)!.AssemblyStoreVersion.Should().Be(buildB.Version);
    }
}
