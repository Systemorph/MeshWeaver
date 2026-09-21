using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The declared-access step must not publish a partition over a protected segment, and its
/// legacy heal must not DELETE the protection</b> — MeshWeaver#4716.
///
/// <para><b>The live shape this is about.</b> The <c>Feedback</c> package is pre-installed and its
/// manifest declares <c>protectedSegments: ["_Submissions"]</c> — a submission inbox on an otherwise
/// public partition. Core could not see that declaration at all
/// (<c>NodeRepoPackageSource.Peek</c> dropped it, the dead-metadata defect class <c>preInstalled</c>,
/// <c>publicSegments</c> and <c>contactEmail</c> each had), so two things followed and both were core's:
/// the partition took the fully-public shape, under which a Public/Anonymous deny is inert on the C#
/// read path (<c>AnonymousCannotReadAProtectedSubmissionTest</c> measures the exposure and its fix);
/// and <c>ContradictingDenies</c> read the plugin machinery's own denies as pre-#902 damage, so the
/// boot repair pass would RETIRE them and rewrite the policy to <c>PublicRead</c>.</para>
///
/// <para><b>Why two arms.</b> The declaration-driven arm is the forward fix. The EVIDENCE-driven arm is
/// the one a live portal actually needs: the boot repair pass re-drives this step from the INSTALL
/// RECORD's stored manifest, and every record stamped before <c>ProtectedSegments</c> was read carries
/// no declaration — so a fix that only reads the manifest would leave every already-installed portal
/// republishing its inbox on the next boot. Both arms must converge on the same nodes.</para>
///
/// <para>Node shapes only. What the shape ANSWERS for an anonymous reader is measured in
/// <c>MeshWeaver.Graph.Test/AnonymousCannotReadAProtectedSubmissionTest</c>, against a mesh that does
/// not grant Public Admin; this fixture's job is that the installer WRITES that shape and never
/// dismantles it.</para>
/// </summary>
public class ADeclaredProtectedSegmentSurvivesTheBootHealTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Declaring = "DeclaringPkg";
    private const string Stale = "StalePkg";
    private const string Closed = "ClosedPkg";
    private const string Inbox = "_Submissions";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private ILogger Logger => Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger<ADeclaredProtectedSegmentSurvivesTheBootHealTest>();

    /// <summary>The manifest of a pre-installed package that declares its inbox protected.</summary>
    private static PackageManifest Manifest(string id, bool declaresProtection) => new()
    {
        Id = id,
        Kind = PackageKind.NodeRepo,
        TargetPartition = id,
        PreInstalled = true,
        ProtectedSegments = declaresProtection ? [Inbox] : [],
    };

    /// <summary>Authoritative single read off storage (never the lagging index); null when absent.</summary>
    private Task<MeshNode?> Read(string path) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1)
            .DefaultIfEmpty(null)
            .Timeout(60.Seconds())
            .Await(TestContext.Current.CancellationToken);

    private Task WriteAsSystem(MeshNode node) =>
        Access.RunAsSystem(() => MeshService.CreateOrUpdateNode(node))
            .Should().Within(60.Seconds())
            .Emit($"writing the precondition node '{node.Path}' must land",
                cancellationToken: TestContext.Current.CancellationToken);

    private Task Establish(PackageManifest manifest, string partition) =>
        Access.RunAsSystem(() => PackageInstaller.EnsureDeclaredAccess(
                Mesh, manifest, partition, Logger))
            .Should().Within(120.Seconds())
            .Emit("the declared-access step must complete",
                cancellationToken: TestContext.Current.CancellationToken);

    private async Task<bool?> PolicyPublicRead(string partition)
    {
        var policy = await Read($"{partition}/_Policy");
        return policy?.ContentAs<PartitionAccessPolicy>(Mesh.JsonSerializerOptions)?.PublicRead;
    }

    private static string Deny(string scope, string subject) => $"{scope}/_Access/{subject}_Access";

    /// <summary>
    /// The DECLARATION-driven arm. A pre-installed manifest naming a protected segment is published
    /// through root Public/Anonymous Viewer GRANTS with a deny pair on the segment and a <c>_Policy</c>
    /// that WITHHOLDS public read — never the blanket <c>PublicRead</c> policy, which would leave the
    /// deny pair inert on the C# read path.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ADeclaredProtectedSegment_IsPublishedThroughGrantsAndNeverThroughAPublicReadPolicy()
    {
        // Referenced so the method honours its own Timeout (xUnit1069); every helper below threads
        // TestContext.Current.CancellationToken through its own await.
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        await WriteAsSystem(new MeshNode(Declaring) { NodeType = "Space", State = MeshNodeState.Active });
        await WriteAsSystem(new MeshNode("Cover", Declaring) { NodeType = "Markdown", State = MeshNodeState.Active });
        await WriteAsSystem(new MeshNode("theirs", $"{Declaring}/{Inbox}")
            { NodeType = "Markdown", State = MeshNodeState.Active });

        await Establish(Manifest(Declaring, declaresProtection: true), Declaring);

        (await PolicyPublicRead(Declaring)).Should().BeFalse(
            "THE assertion: the policy must exist (it is the declared-access post-condition marker) and "
            + "must WITHHOLD public read. A PublicRead policy here is the exposure — the C# evaluator ORs "
            + "it in after the deny subtraction, so the deny pair below would protect nothing on that "
            + "path while the SQL fold honoured it: one segment, two answers (MeshWeaver#4716)");

        (await Read(Deny($"{Declaring}/{Inbox}", WellKnownUsers.Public))).Should().NotBeNull(
            "the declared segment is gated for Public…");
        (await Read(Deny($"{Declaring}/{Inbox}", WellKnownUsers.Anonymous))).Should().NotBeNull(
            "…and for Anonymous. The installer's own child walk deliberately skips `_` satellites, so "
            + "without the declaration being READ nothing in core ever writes these");

        (await Read(Deny(Declaring, WellKnownUsers.Public))).Should().NotBeNull(
            "and the catalog half is still published — through a root GRANT, the one mechanism the C# "
            + "evaluator and the SQL longest-prefix fold resolve identically");
        (await Read(Deny(Declaring, WellKnownUsers.Anonymous))).Should().NotBeNull(
            "both well-known subjects, or a logged-out visitor loses the public half");

        // Idempotence: the step runs on every install path AND as a boot re-assert, so a second pass
        // must not undo the first. This is the shape the repair pass drives.
        await Establish(Manifest(Declaring, declaresProtection: true), Declaring);
        (await PolicyPublicRead(Declaring)).Should().BeFalse(
            "a re-assert converges — the protection is not a one-shot that the next boot undoes");
        (await Read(Deny($"{Declaring}/{Inbox}", WellKnownUsers.Public))).Should().NotBeNull(
            "and the deny pair survives its own re-assert");
    }

    /// <summary>
    /// The create-only rule, which is the difference between MOVING a publication and ADDING one. A
    /// pre-installed partition whose policy ALREADY withholds public read is one this step leaves
    /// closed, so a declaration must gate the segment and open nothing.
    ///
    /// <para>That is not hypothetical: on the control instance memex.systemorph.com, read 2026-09-21,
    /// <c>Feedback/_Policy</c> carries no <c>publicRead</c> and the partition has no
    /// <c>_Submissions</c> at all. Publishing it from the declaration would have handed an anonymous
    /// reader a partition somebody had closed — a widening introduced by the fix for an exposure,
    /// which is the worst shape a security change can take.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ADeclaredProtectedSegment_OnAPartitionThatWithholdsPublicRead_IsGatedButNotOpened()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        await WriteAsSystem(new MeshNode(Closed) { NodeType = "Space", State = MeshNodeState.Active });
        await WriteAsSystem(new MeshNode("Cover", Closed) { NodeType = "Markdown", State = MeshNodeState.Active });
        await WriteAsSystem(new MeshNode("theirs", $"{Closed}/{Inbox}")
            { NodeType = "Markdown", State = MeshNodeState.Active });
        await WriteAsSystem(new MeshNode("_Policy", Closed)
        {
            NodeType = "PartitionAccessPolicy",
            Name = "Access Policy",
            State = MeshNodeState.Active,
            Content = new PartitionAccessPolicy { PublicRead = false },
        });

        await Establish(Manifest(Closed, declaresProtection: true), Closed);

        (await Read(Deny($"{Closed}/{Inbox}", WellKnownUsers.Public))).Should().NotBeNull(
            "the gate is written either way — a deny can only ever NARROW, so establishing it needs no "
            + "permission to widen anything");
        (await Read(Deny($"{Closed}/{Inbox}", WellKnownUsers.Anonymous))).Should().NotBeNull(
            "both halves of the pair");

        (await Read(Deny(Closed, WellKnownUsers.Public))).Should().BeNull(
            "THE assertion: no root grant. This step would not have opened this partition with a policy "
            + "either (create-only leaves a policy that withholds public read alone), so it must not "
            + "open it with grants — the shape exists to MOVE a publication off the policy, never to "
            + "add one (MeshWeaver#4716)");
        (await Read(Deny(Closed, WellKnownUsers.Anonymous))).Should().BeNull(
            "and the same for Anonymous");
        (await PolicyPublicRead(Closed)).Should().BeFalse(
            "the policy is untouched — it already said what the shape needs it to say");
    }

    /// <summary>
    /// 🚨 <b>THE CASE THAT WOULD HAVE CAUGHT IT.</b> The EVIDENCE-driven arm: the partition is in the
    /// protected shape and the manifest driving the re-assert declares NOTHING — the state of every
    /// portal whose install record was stamped before <c>ProtectedSegments</c> was read. The step must
    /// leave the denies alone and must not write <c>PublicRead</c>.
    ///
    /// <para>On <c>main</c> both halves fail: <c>ContradictingDenies</c> matches the two satellite denies
    /// as the pre-#902 gate, <c>Retire</c> DELETES them, and the policy is healed to
    /// <c>PublicRead = true</c> — a submission inbox republished by a boot pass, logged at Information
    /// as a legacy migration.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnUndeclaredButProtectedSegment_IsNotRetiredAndNotRepublishedByTheHeal()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        await WriteAsSystem(new MeshNode(Stale) { NodeType = "Space", State = MeshNodeState.Active });
        await WriteAsSystem(new MeshNode("Cover", Stale) { NodeType = "Markdown", State = MeshNodeState.Active });
        await WriteAsSystem(new MeshNode("theirs", $"{Stale}/{Inbox}")
            { NodeType = "Markdown", State = MeshNodeState.Active });

        // The policy the partition already carries, WITHHOLDING public read — the live shape on
        // memex.meshweaver.cloud (`Feedback/_Policy`, read 2026-09-21: content with no `publicRead`,
        // written by `system-security` 1.6 s before the deny pair below). Without it `current` is null
        // here, the heal's retire arm is never reached, and the assertion on the denies surviving
        // passes for the wrong reason.
        await WriteAsSystem(new MeshNode("_Policy", Stale)
        {
            NodeType = "PartitionAccessPolicy",
            Name = "Access Policy",
            State = MeshNodeState.Active,
            Content = new PartitionAccessPolicy { PublicRead = false },
        });

        // The protection another component put there — the plugin machinery's ProtectedSegments arm.
        await WriteAsSystem(PackageInstaller.ViewerAssignment(
            $"{Stale}/{Inbox}", WellKnownUsers.Public, denied: true));
        await WriteAsSystem(PackageInstaller.ViewerAssignment(
            $"{Stale}/{Inbox}", WellKnownUsers.Anonymous, denied: true));

        // …and one deny the installer's OWN scoped shape could have written: pre-#902 damage on an
        // ordinary child. The heal must still retire THAT — a control on each side of the split, so the
        // fix cannot be "stop healing" wearing the right answer's clothes.
        await WriteAsSystem(new MeshNode("Legacy", Stale) { NodeType = "Markdown", State = MeshNodeState.Active });
        await WriteAsSystem(PackageInstaller.ViewerAssignment(
            $"{Stale}/Legacy", WellKnownUsers.Public, denied: true));

        await Establish(Manifest(Stale, declaresProtection: false), Stale);

        (await Read(Deny($"{Stale}/{Inbox}", WellKnownUsers.Public))).Should().NotBeNull(
            "THE assertion: a deny on an `_` satellite is outside the legacy fingerprint BY "
            + "CONSTRUCTION — the installer's own child walk has never written one — so it can only be "
            + "a live protection, and retiring it deletes security state a boot pass had no business "
            + "touching (MeshWeaver#4716)");
        (await Read(Deny($"{Stale}/{Inbox}", WellKnownUsers.Anonymous))).Should().NotBeNull(
            "both halves of the pair, or a logged-out visitor reads the inbox through the surviving gap");

        (await PolicyPublicRead(Stale)).Should().BeFalse(
            "and the OTHER half of the same defect: writing PublicRead here needs no delete at all to "
            + "republish the segment, because under that policy the surviving denies withhold nothing on "
            + "the C# read path. So the step must decline the blanket policy purely on the evidence, "
            + "without any manifest telling it to");

        (await Read(Deny($"{Stale}/Legacy", WellKnownUsers.Public))).Should().BeNull(
            "the control on the other side of the split: a deny on an ORDINARY child IS the pre-#902 "
            + "fingerprint (#902's own incident — 136 legacy denies over 8 pre-installed partitions), "
            + "and the heal still retires it. Without this the narrowing could have been a blanket "
            + "'never retire anything' and read as a pass");
    }
}

/// <summary>
/// The pure discriminators, pinnable without a mesh — what separates a live protection from the
/// pre-#902 fingerprint, and what a manifest's declaration resolves to (MeshWeaver#4716).
/// </summary>
public class ProtectedSegmentDiscriminatorTest
{
    private const string Partition = "Feedback";

    [Theory]
    // The live shape on memex.meshweaver.cloud, read 2026-09-21: the deny pair the plugin machinery
    // wrote for Feedback's declared `_Submissions`. Never the installer's own — it skips `_` segments.
    [InlineData("Feedback/_Submissions/_Access/Public_Access", true)]
    [InlineData("Feedback/_Submissions/_Access/Anonymous_Access", true)]
    // Deeper still inside a satellite — the same conclusion.
    [InlineData("Feedback/_Submissions/Triaged/_Access/Public_Access", true)]
    // The partition's OWN _Access container is not a satellite scope in this sense: a deny there gates
    // the partition root, which IS the shape the heal exists to undo.
    [InlineData("Feedback/_Access/Public_Access", false)]
    // An ordinary child — the pre-#902 fingerprint proper.
    [InlineData("Feedback/Issues/_Access/Public_Access", false)]
    [InlineData("Feedback/Issues/Deep/_Access/Anonymous_Access", false)]
    // Outside the partition entirely.
    [InlineData("Other/_Submissions/_Access/Public_Access", false)]
    public void ASatelliteScopedDeny_IsRecognisedExactlyWhereTheInstallerCouldNotHaveWrittenIt(
        string path, bool expected) =>
        PackageInstaller.IsSatelliteScopedDeny(path, Partition).Should().Be(expected,
            "the split decides whether a boot pass DELETES the node, so it has to be the constructive "
            + "one: may this sweep's own past self have written it?");

    [Fact]
    public void ADeclaredProtectedSegment_ResolvesToAFullPathUnderThePartition() =>
        PackageInstaller.DeclaredProtectedPaths(
                new PackageManifest { Id = Partition, ProtectedSegments = ["_Submissions"] }, Partition)
            .Should().Equal(["Feedback/_Submissions"],
                "the `_` prefix is NOT stripped and NOT skipped — a submission inbox lives on a "
                + "satellite, and skipping it is what left the declaration unhonoured");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("_Sub/missions")]
    public void AMalformedDeclaration_IsDroppedRatherThanEscapingThePartition(string segment) =>
        PackageInstaller.DeclaredProtectedPaths(
                new PackageManifest { Id = Partition, ProtectedSegments = [segment] }, Partition)
            .Should().BeEmpty(
                "one segment, inside this partition — never a traversal and never a foreign path");

    [Fact]
    public void ADeclaringPreInstalledManifest_KeepsThePolicyAsItsDeclaredAccessMarker() =>
        PackageInstaller.DeclaredAccessMarker(
                new PackageManifest { Id = Partition, PreInstalled = true, ProtectedSegments = ["_Submissions"] },
                Partition)
            .Should().Be("Feedback/_Policy",
                "the open-with-protected-segments shape writes a _Policy too — one that withholds "
                + "PublicRead — precisely so the post-condition marker keeps holding. A marker naming a "
                + "node the chosen shape does not write turns the post-condition into an error on a "
                + "correct install");

    [Fact]
    public void ABothDeclaringPreInstalledManifest_TakesTheGrantMarker() =>
        PackageInstaller.DeclaredAccessMarker(
                new PackageManifest
                {
                    Id = Partition,
                    PreInstalled = true,
                    PublicSegments = ["Issues"],
                    ProtectedSegments = ["_Submissions"],
                },
                Partition)
            .Should().Be("Feedback/_Access/Public_Access",
                "a protected segment forces the SCOPED shape even for a pre-installed manifest that also "
                + "declares public segments, and that shape writes root grants and NO policy — so a "
                + "marker naming _Policy would report a CORRECT install as a failure. The marker must "
                + "mirror the branch predicate, never restate it (found in review on MeshWeaver#4716)");

    [Fact]
    public void ADeclarationOfNothing_IsNotADeclarationOfProtection() =>
        PackageInstaller.DeclaredProtectedPaths(new PackageManifest { Id = Partition }, Partition)
            .Should().BeEmpty(
                "the default must never route a package onto the grant-based shape by accident — "
                + "every partition that does not ask for an exception keeps the fully-public policy");
}
