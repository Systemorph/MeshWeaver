using System;
using System.Collections.Immutable;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 Issue #5057, the residual <c>#5201</c> recorded but did not repair: <b>a release that lands
/// after BOTH bounds is adopted when it lands</b>.
///
/// <para>The settle and its same-id re-cut each wait <see cref="NodeTypeBuildState.CreateBound"/>. A
/// landing slower than both ends the settle with <see cref="NodeTypeDefinition.UnreleasedBuildPath"/>
/// stamped, and then the node lands at exactly that path. It was measured doing so on the control
/// instance: <c>Hosting/TriageItem/Release/20260923055416-Hd-IFSiA</c> landed 21 s after its id was
/// minted, and #5474 folded 69 more such lines on the image carrying #5201. Before
/// <see cref="LateReleaseAdoption"/> nothing read the stamp again, so the type kept binding the
/// previous build's release.</para>
///
/// <para><b>The control, on a real mesh.</b> A NodeType is seeded in exactly the stamped state: the
/// settle is over, <c>latestReleasePath</c> names the previous build's release, and
/// <c>unreleasedBuildPath</c> names the id the attempts were minting. Its owner is activated, which
/// installs the watchers <c>MeshDataSource</c> installs on every NodeType hub. Then the release node
/// is created at the stamped path, which is the late landing. The pointer must follow. Without the
/// watcher registered this test fails at the <c>Match</c>, because nothing else ever writes the
/// pointer.</para>
/// </summary>
public class ALateReleaseIsAdoptedWhenItLandsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PreviousRelease = "Release/20260920072917-Djt66iSB";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static NodeTypeDefinition Stamped(string typePath, string unreleased) => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Ok,
        LastCompiledVersion = 3326,
        LatestReleasePath = $"{typePath}/{PreviousRelease}",
        UnreleasedBuildPath = unreleased,
        UnreleasedBuildReason = "the create did not land within 00:00:10",
        ReleaseNotes = "notes for the release that landed late",
        CompiledSources = ImmutableDictionary<string, long>.Empty,
    };

    private static string ReleasePathFor(string typePath, string id) =>
        $"{typePath}/{GraphNodeTypeNames.ReleaseSegment}/{id}";

    private static MeshNode ReleaseNodeAt(string releasePath, string typePath)
    {
        var releaseNamespace = $"{typePath}/{GraphNodeTypeNames.ReleaseSegment}";
        var version = releasePath[(releaseNamespace.Length + 1)..];
        return new MeshNode(version, releaseNamespace)
        {
            Name = $"Release {version}",
            NodeType = GraphNodeTypeNames.Release,
            MainNode = typePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeRelease
            {
                Path = releasePath,
                NodeTypePath = typePath,
                Release = version[15..],
                Version = version,
                FrameworkVersion = "3.0.0.0",
                CreatedAt = DateTimeOffset.UtcNow,
                AssemblyStoreVersion = 3326,
                Status = "Succeeded",
            },
        };
    }

    private async Task SeedAsync(string typePath, string unreleased)
    {
        var typeNode = MeshNode.FromPath(typePath) with
        {
            Name = typePath,
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = Stamped(typePath, unreleased),
        };
        await MeshService.CreateNode(typeNode)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the stamped NodeType must exist", cancellationToken: TestContext.Current.CancellationToken);

        // Reading the node through the stream activates its owner, and with it the watchers.
        await Mesh.GetMeshNodeStream(typePath)
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                        && string.Equals(d.UnreleasedBuildPath, unreleased, StringComparison.Ordinal),
                "the owner serves the stamped state");
    }

    /// <summary>
    /// 🚨 THE CASE: the settle is over, the stamp names the path, the node lands there, and the
    /// pointer follows it with the stamp cleared.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARelease_LandingAtTheStampedPath_IsAdopted()
    {
        var typePath = $"{TestPartition}/LateRelease{Guid.NewGuid().ToString("N")[..8]}";
        var unreleased = ReleasePathFor(typePath, "20260923055416-Hd-IFSiA");
        await SeedAsync(typePath, unreleased);

        // The late landing.
        await MeshService.CreateNode(ReleaseNodeAt(unreleased, typePath))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the late create lands", cancellationToken: TestContext.Current.CancellationToken);

        var adopted = await Mesh.GetMeshNodeStream(typePath)
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                        && string.Equals(d.LatestReleasePath, unreleased, StringComparison.Ordinal),
                "a release that landed after the settle stopped waiting must become the type's "
                + "release when it lands. Nothing else ever writes the pointer again (#5057)");

        var def = adopted.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
        def.UnreleasedBuildPath.Should().BeNull("the build has a release now");
        def.UnreleasedBuildReason.Should().BeNull("the reason never outlives the path");
        def.ReleaseNotes.Should().BeNull("the notes were written for this release and are spent on it");
        def.LastCompiledVersion.Should().Be(3326L, "adoption moves the pointer and touches no build state");
    }

    /// <summary>
    /// The negative half: a release at some OTHER path is not the stamped one, so the stamp stands
    /// and the pointer does not move. The watch is on one path and never on "any release".
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AReleaseAtAnotherPath_IsNotAdopted_AndTheStampStands()
    {
        var typePath = $"{TestPartition}/LateReleaseOther{Guid.NewGuid().ToString("N")[..8]}";
        var unreleased = ReleasePathFor(typePath, "20260923055416-Hd-IFSiA");
        await SeedAsync(typePath, unreleased);

        await MeshService.CreateNode(ReleaseNodeAt(ReleasePathFor(typePath, "20260923055426-OtherByt"), typePath))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("an unrelated release lands", cancellationToken: TestContext.Current.CancellationToken);

        await Mesh.GetMeshNodeStream(typePath)
            .Where(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                        && (d.UnreleasedBuildPath is null
                            || !string.Equals(d.LatestReleasePath, $"{typePath}/{PreviousRelease}",
                                StringComparison.Ordinal)))
            .Should()
            .NotEmit(TestTimeouts.Quick, "a release for other bytes must never be adopted");
    }

    // ───────────── the decision, pure ─────────────

    /// <summary>A newer prebuilt adoption must retire the previous build's pending release.</summary>
    [Fact(Timeout = 120_000)]
    public async Task APrebuiltAdoption_RetiresThePreviousBuildsPendingRelease()
    {
        var typePath = $"{TestPartition}/SupersededRelease{Guid.NewGuid().ToString("N")[..8]}";
        var unreleased = ReleasePathFor(typePath, "20260923055416-Hd-IFSiA");
        await SeedAsync(typePath, unreleased);

        var seedHub = Mesh.GetHostedHub(new Address("late-release-seed", Guid.NewGuid().ToString("N")),
            c => c.AddData().WithGraphTypes(), HostedHubCreation.Always)
            ?? throw new InvalidOperationException("The seed hub must exist");
        var bytes = File.ReadAllBytes(typeof(ALateReleaseIsAdoptedWhenItLandsTest).Assembly.Location);
        var mvid = ServedBuildIdentity.OfBytes(bytes);
        mvid.Should().NotBeNullOrEmpty();
        var outcome = await PrebuiltAssemblySeeder.SeedDetailed(seedHub, typePath, bytes, null,
                PrebuiltAssemblySeeder.LiveFrameworkMvid, null, null, sourceFingerprint: null)
            .Should().Within(TestTimeouts.WriteConvergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        outcome.Should().Be(PrebuiltAssemblySeeder.SeedOutcome.Adopted);

        var adopted = await Mesh.GetMeshNodeStream(typePath).Should().Within(TestTimeouts.WriteConvergence)
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                        && d.LatestAssemblyMvid == mvid);
        var definition = adopted.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
        definition.LastCompiledVersion.Should().NotBe(3326L, "the prebuilt bundle replaced the old build");
        definition.UnreleasedBuildPath.Should().BeNull(
            "the pending release describes the old bytes, so its late arrival must not move the new build's pointer");
        definition.UnreleasedBuildReason.Should().BeNull("the reason belongs to the retired marker");
        definition.LatestReleasePath.Should().Be($"{typePath}/{PreviousRelease}");
        definition.ReleaseNotes.Should().Be("notes for the release that landed late");

        await MeshService.CreateNode(ReleaseNodeAt(unreleased, typePath))
            .Should().Within(TestTimeouts.Convergence).Emit();
        await Mesh.GetMeshNodeStream(typePath)
            .Where(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)?.LatestReleasePath == unreleased)
            .Should().NotEmit(TestTimeouts.Quick, "the old release cannot become the adopted bundle's release");
    }

    /// <summary>A replay returning the pending build's store coordinates still owes that release.</summary>
    [Fact]
    public void AReplayOfThePendingBuild_PreservesItsMarker()
    {
        var location = new AssemblyStoreLocation("local-cache", "assemblies", "T/v1-build.dll");
        var hash = NodeTypeBuildState.ContentHashOf(new NodeCompilationResult(
            location.LocalPath, [], Collection: location.Collection, ContentPath: location.ContentPath));
        var definition = Stamped("T", $"T/Release/20260923055416-{hash}");
        PrebuiltAssemblySeeder.PendingReleaseWasSuperseded(definition, "T", location).Should().BeFalse();
        PrebuiltAssemblySeeder.PendingReleaseWasSuperseded(definition, "T",
            location with { ContentPath = "T/v2-build.dll" }).Should().BeTrue();
        PrebuiltAssemblySeeder.PendingReleaseWasSuperseded(definition, "T",
            location with { Collection = "" }).Should().BeFalse("missing coordinates prove nothing");
    }

    /// <summary>The real seeder must preserve and eventually adopt a same-coordinate pending release.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ASeedReturningThePendingCoordinates_KeepsTheLateReleaseWatch()
    {
        var typePath = $"{TestPartition}/ReplayRelease{Guid.NewGuid().ToString("N")[..8]}";
        await SeedAsync(typePath, ReleasePathFor(typePath, "20260923055416-Hd-IFSiA"));
        var settled = await Mesh.GetMeshNodeStream(typePath).Should().Within(TestTimeouts.Convergence)
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)?.CurrentSourceVersions is not null);
        var bytes = File.ReadAllBytes(typeof(ALateReleaseIsAdoptedWhenItLandsTest).Assembly.Location);
        // Store the bytes for the revision produced by the next owner write. The version assertion
        // below proves that SeedDetailed really reads this first-write-wins key.
        var storeVersion = settled.Version + 1;
        var location = await Mesh.ServiceProvider.GetRequiredService<IAssemblyStore>()
            .PutWithLocation(typePath, storeVersion, bytes, null)
            .Should().Within(TestTimeouts.WriteConvergence).Emit();
        var hash = NodeTypeBuildState.ContentHashOf(new NodeCompilationResult(
            location.LocalPath, [], Collection: location.Collection, ContentPath: location.ContentPath));
        var pending = ReleasePathFor(typePath, $"20260923055416-{hash}");
        await Mesh.GetMeshNodeStream(typePath).Update(current => current with
        {
            Content = current.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)! with
            {
                UnreleasedBuildPath = pending,
            },
        }).Should().Within(TestTimeouts.WriteConvergence).Emit();
        await Mesh.GetMeshNodeStream(typePath).Should().Within(TestTimeouts.WriteConvergence)
            .Match(n => n.Version == storeVersion
                        && n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)?.UnreleasedBuildPath == pending,
                "the owner must publish the revision whose store key the seed will reuse");
        var seedHub = Mesh.GetHostedHub(new Address("late-release-replay", Guid.NewGuid().ToString("N")),
            c => c.AddData().WithGraphTypes(), HostedHubCreation.Always)!;
        var outcome = await PrebuiltAssemblySeeder.SeedDetailed(seedHub, typePath, bytes, null,
                PrebuiltAssemblySeeder.LiveFrameworkMvid, null, null, sourceFingerprint: null)
            .Should().Within(TestTimeouts.WriteConvergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        outcome.Should().Be(PrebuiltAssemblySeeder.SeedOutcome.Adopted);
        var seeded = await Mesh.GetMeshNodeStream(typePath).Should().Within(TestTimeouts.WriteConvergence)
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)?.LatestAssemblyPath == location.ContentPath);
        seeded.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!.UnreleasedBuildPath.Should().Be(pending);

        var release = ReleaseNodeAt(pending, typePath);
        await MeshService.CreateNode(release with
        {
            Content = release.ContentAs<NodeTypeRelease>(Mesh.JsonSerializerOptions)! with { AssemblyStoreVersion = storeVersion },
        }).Should().Within(TestTimeouts.Convergence).Emit();
        await Mesh.GetMeshNodeStream(typePath).Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                        && d.LatestReleasePath == pending && d.UnreleasedBuildPath is null,
                "the retained marker must keep the watch live until this build's release arrives");
    }

    /// <summary>
    /// 🚨 #6056 — A STAMP WHOSE CREATE WAS NEVER WRITTEN IS CUT BY THE NEXT ACTIVATION. The
    /// incident: a replica being rolled away settled two NodeTypes, and both the release create and
    /// its same-id re-cut were answered "cancelled before it completed" by the draining host. The
    /// stamp landed, the node never would, and the only thing that ever repaired it was a recompile.
    /// Seeded here exactly so: a settled, usable build of THIS framework whose bytes are in the
    /// assembly store, stamped with an id for those bytes, and no release node anywhere. Activating
    /// the owner must cut that release at the stamped id and adopt it. Without
    /// <see cref="LateReleaseAdoption.CompleteInheritedRelease"/> this fails at the <c>Match</c>:
    /// the landing watch waits for a create nobody will ever make.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnInheritedStamp_WhoseCreateNeverLanded_IsCutByTheNextActivation()
    {
        var typePath = $"{TestPartition}/InheritedRelease{Guid.NewGuid().ToString("N")[..8]}";
        var bytes = File.ReadAllBytes(typeof(ALateReleaseIsAdoptedWhenItLandsTest).Assembly.Location);
        var location = await Mesh.ServiceProvider.GetRequiredService<IAssemblyStore>()
            .PutWithLocation(typePath, 3729, bytes, null)
            .Should().Within(TestTimeouts.WriteConvergence).Emit();
        var hash = NodeTypeBuildState.ContentHashOf(new NodeCompilationResult(
            location.LocalPath, [], Collection: location.Collection, ContentPath: location.ContentPath));
        var unreleased = ReleasePathFor(typePath, $"20261003231557-{hash}");

        var typeNode = MeshNode.FromPath(typePath) with
        {
            Name = typePath,
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = Stamped(typePath, unreleased) with
            {
                LastCompiledVersion = 3729,
                LatestAssemblyCollection = location.Collection,
                LatestAssemblyPath = location.ContentPath,
                CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
                UnreleasedBuildReason = $"the create at '{unreleased}' failed: InvalidOperationException: "
                    + $"Node creation at '{unreleased}' was cancelled before it completed.",
            },
        };
        await MeshService.CreateNode(typeNode)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the stamped NodeType must exist", cancellationToken: TestContext.Current.CancellationToken);

        var adopted = await Mesh.GetMeshNodeStream(typePath)
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                        && string.Equals(d.LatestReleasePath, unreleased, StringComparison.Ordinal),
                "the activation that inherits a build with no release must cut it at the stamped id "
                + "and adopt it — nothing else ever creates that node (#6056)");
        var def = adopted.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
        def.UnreleasedBuildPath.Should().BeNull("the build has a release now");
        def.UnreleasedBuildReason.Should().BeNull("the reason never outlives the path");
        def.LastCompiledVersion.Should().Be(3729L, "completing the release recompiles nothing");

        var release = await Mesh.GetMeshNodeStream(unreleased)
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n.ContentAs<NodeTypeRelease>(Mesh.JsonSerializerOptions) is not null,
                "the release node exists at the stamped id");
        var content = release.ContentAs<NodeTypeRelease>(Mesh.JsonSerializerOptions)!;
        content.AssemblyStoreVersion.Should().Be(3729L, "the release names the recorded build");
        content.AssemblyCollection.Should().Be(location.Collection);
        content.AssemblyContentPath.Should().Be(location.ContentPath);
    }

    [Fact]
    public void InheritedObligation_NamesTheStamp_OnlyForTheRecordedBytesOfThisFramework()
    {
        const string collection = "assemblies";
        const string contentPath = "T/v1-build.dll";
        var hash = NodeTypeBuildState.ContentHashOf(new NodeCompilationResult(
            null, [], Collection: collection, ContentPath: contentPath));
        var stamped = $"T/Release/20261003231557-{hash}";
        var framework = NodeTypeCompilationHelpers.FrameworkVersion;
        var owed = Stamped("T", stamped) with
        {
            LatestAssemblyCollection = collection,
            LatestAssemblyPath = contentPath,
            CompiledFrameworkVersion = framework,
        };

        LateReleaseAdoption.InheritedObligation(owed, "T", framework).Should().Be(stamped);
        LateReleaseAdoption.InheritedObligation(owed with { UnreleasedBuildPath = null }, "T", framework)
            .Should().BeNull("no stamp, no obligation");
        LateReleaseAdoption.InheritedObligation(owed with { CompilationStatus = CompilationStatus.Compiling }, "T", framework)
            .Should().BeNull("a compile in flight rewrites the stamp at its own settle");
        LateReleaseAdoption.InheritedObligation(owed, "T", framework + "-other")
            .Should().BeNull("a build of another framework is about to be recompiled, not released");
        LateReleaseAdoption.InheritedObligation(owed with { LatestAssemblyPath = "T/v2-build.dll" }, "T", framework)
            .Should().BeNull("a stamp naming other bytes is never cut for these");
        LateReleaseAdoption.InheritedObligation(owed with { LatestAssemblyCollection = null }, "T", framework)
            .Should().BeNull("without store coordinates there are no recorded bytes to release");
        LateReleaseAdoption.InheritedObligation(null, "T", framework).Should().BeNull();
    }

    [Fact]
    public void Adopt_MovesThePointer_WhenTheStampNamesThePath()
    {
        var def = Stamped("T", "T/Release/20260923055416-Hd-IFSiA");
        var adopted = LateReleaseAdoption.Adopt(def, "T/Release/20260923055416-Hd-IFSiA");
        adopted.Should().NotBeNull();
        adopted!.LatestReleasePath.Should().Be("T/Release/20260923055416-Hd-IFSiA");
        adopted.UnreleasedBuildPath.Should().BeNull();
        adopted.UnreleasedBuildReason.Should().BeNull();
        adopted.ReleaseNotes.Should().BeNull();
    }

    [Theory]
    [InlineData("T/Release/20260923055426-OtherByt")]
    [InlineData("t/release/20260923055416-hd-ifsia")]
    [InlineData("")]
    public void Adopt_LeavesTheNodeAlone_ForAnyOtherPath(string landed)
        => LateReleaseAdoption.Adopt(Stamped("T", "T/Release/20260923055416-Hd-IFSiA"), landed)
            .Should().BeNull("only the stamped id names these bytes");

    [Fact]
    public void Adopt_LeavesTheNodeAlone_WhenALaterSettleClearedTheStamp()
    {
        var cleared = Stamped("T", "T/Release/20260923055416-Hd-IFSiA") with
        {
            UnreleasedBuildPath = null,
            UnreleasedBuildReason = null,
        };
        LateReleaseAdoption.Adopt(cleared, "T/Release/20260923055416-Hd-IFSiA")
            .Should().BeNull("the stamp describes the current build only; a later settle owns the pointer");
        LateReleaseAdoption.Adopt(null, "T/Release/20260923055416-Hd-IFSiA").Should().BeNull();
    }
}
