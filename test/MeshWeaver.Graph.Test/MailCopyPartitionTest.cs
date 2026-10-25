using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.MailCopy;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The copies live in a partition of their OWN per owner, <c>{owner}_MailCopy</c> (owner, 2026-10-25: "a dedicated
/// partition just for the inbox for every user"), never in the person's home partition: switching a copy on makes
/// that partition — root, routing definition, and the owner's Viewer grant as its one grant — and a copy still
/// filed at the legacy place, <c>{owner}/_MailCopy</c>, is moved there with its generation, cursor, copies and
/// files, and then removed. On a mesh with row-level security and no blanket grant (<c>ConfigureMeshBase</c>).
/// </summary>
public class MailCopyPartitionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder).AddMailCopyTypes();

    private async Task<string> SeedOwner(CancellationToken ct)
    {
        var owner = $"user{Guid.NewGuid():N}"[..12];
        await SeedTopLevel(MeshNode.FromPath(owner) with
        {
            Name = "Jane Roe",
            NodeType = UserNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new User { Email = "jane@acme.example", FullName = "Jane Roe" },
        }).WaitAsync(ct);
        return owner;
    }

    private IDisposable AsSystem() => Mesh.ServiceProvider.GetRequiredService<AccessService>().ImpersonateAsSystem();

    private async Task<MeshNode?> Listed(string path, CancellationToken ct)
    {
        using var _ = AsSystem();
        var change = await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}") with { Limit = 1 })
            .Take(1).Timeout(Bound).Await(ct);
        return change.Items.FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.Ordinal));
    }

    // A node the test expects to EXIST: found through the listing (waiting while the index catches up), then read
    // from its stream — never a first query frame taken as the node's post-write state.
    private async Task<MeshNode> Present(string path, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + Bound;
        while (await Listed(path, ct) is null)
        {
            DateTimeOffset.UtcNow.Should().BeBefore(until, $"'{path}' should exist");
            await Task.Delay(100, ct);
        }
        using var _ = AsSystem();
        return await Mesh.GetMeshNodeStream(path).Take(1).Timeout(Bound).Await(ct);
    }

    // A node the test expects to be GONE: waits until the listing no longer has it, within the bound.
    private async Task Absent(string path, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + Bound;
        while (await Listed(path, ct) is not null)
        {
            DateTimeOffset.UtcNow.Should().BeBefore(until, $"'{path}' should be gone");
            await Task.Delay(100, ct);
        }
    }

    [Fact]
    public void TheCopyPaths_AreTheOwnersOwnPartition()
    {
        MailCopyNodeTypes.NamespaceFor("jroe").Should().Be("jroe_MailCopy");
        MailCopyNodeTypes.SourcePath("jroe", "outlook").Should().Be("jroe_MailCopy/outlook");
        MailCopyNodeTypes.CopyPath("jroe", "outlook", "g1", "AAMk-1").Should().StartWith("jroe_MailCopy/outlook/");
        MailCopyNodeTypes.LegacySourcePath("jroe", "outlook").Should().Be("jroe/_MailCopy/outlook",
            "the move reads the place the copies had before");
        MailCopyNodeTypes.CopyPath("jroe", "outlook", "g1", "AAMk-1").Split('/')[0]
            .Should().NotBe("jroe", "a copy is never in the person's home partition");
    }

    [Theory]
    [InlineData("jroe", true)]
    [InlineData("felice.buergi", true)]
    [InlineData("jroe_MailCopy", false)]
    [InlineData("JROE_MAILCOPY", false)]
    [InlineData("a/b", false)]
    [InlineData("jane@acme", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    public void AnOwner_WhoseCopyPartitionCouldNotExist_IsRefused(string owner, bool valid)
    {
        MailCopyNodeTypes.IsValidOwner(owner).Should().Be(valid,
            "the HOME partition of 'jroe_MailCopy' would be the copy partition of 'jroe', and a copy partition is a "
            + "partition segment: at most 63 UTF-8 bytes (54 for the owner), letters, digits, '.', '-', '_'");
        if (valid)
            PartitionDefinition.IsValidPartitionSegment(MailCopyNodeTypes.NamespaceFor(owner)).Should().BeTrue();
    }

    [Theory]
    [InlineData("jroe_MailCopy", "jroe")]
    [InlineData("jroe/_MailCopy", "jroe")]
    [InlineData("felice.buergi_MailCopy", "felice.buergi")]
    [InlineData("jroe", null)]
    [InlineData("jroe/Notes", null)]
    [InlineData("Org/jroe/_MailCopy", null)]
    [InlineData("_MailCopy", null)]
    [InlineData("x_MailCopy_MailCopy", null)]
    [InlineData(null, null)]
    public void TheOwnerOfASource_IsReadFromEitherPlace(string? @namespace, string? owner)
        => MailCopyNodeTypes.OwnerOfSourceNamespace(@namespace).Should().Be(owner);

    [Fact(Timeout = 120_000)]
    public async Task ACopyNotYetMoved_IsStillListed_ForTheOwnerAndForTheKeeper()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await store.Create(owner, new MailCopySource { Provider = "outlook", Generation = "g1" }).Timeout(Bound).Await(ct);
        using (AsSystem())
            await Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(new MeshNode("gmail", MailCopyNodeTypes.LegacyNamespaceFor(owner))
            { NodeType = MailCopyNodeTypes.SourceNodeType, State = MeshNodeState.Active, Content = new MailCopySource { Provider = "gmail", Generation = "g1" } })
                .Timeout(Bound).Await(ct);

        Task<ImmutableList<string>> keys;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            keys = store.SourceKeys(owner).Timeout(Bound).Await(ct);
        (await keys).Order().Should().Equal(["gmail", "outlook"], "the legacy copy is still on, so Settings still lists it — and can delete it");

        var all = await store.EnabledSources().Timeout(Bound).Await(ct);
        all.Should().Contain(new MailCopySourceRef(owner, "outlook"), "a copy in the copy partition is resumed")
            .And.Contain(new MailCopySourceRef(owner, "gmail"), "and so is one still at the legacy place — resuming it is what moves it");
    }

    [Fact]
    public void Relocated_ReRootsOnlyTheStoredPathsUnderTheMovedNode()
    {
        var copy = new MailMessageCopy
        {
            Provider = "outlook",
            Generation = "g1",
            MessageId = "m1",
            Attachments =
            [
                new MailAttachmentCopy { AttachmentId = "a1", StoredPath = "jroe/_MailCopy/outlook/c1/attachments/f1/deck.pptx" },
                new MailAttachmentCopy { AttachmentId = "a2", SkippedReason = MailAttachmentSkips.Inline },
                new MailAttachmentCopy { AttachmentId = "a3", StoredPath = "jroe/_MailCopy/outlook/c10/attachments/f3/x.pdf" },
            ],
        };

        var moved = MailCopyNodeTypes.Relocated(copy, "jroe/_MailCopy/outlook/c1", "jroe_MailCopy/outlook/c1");

        moved.Attachments![0].StoredPath.Should().Be("jroe_MailCopy/outlook/c1/attachments/f1/deck.pptx");
        moved.Attachments[1].StoredPath.Should().BeNull("a skipped attachment has no file");
        moved.Attachments[2].StoredPath.Should().Be("jroe/_MailCopy/outlook/c10/attachments/f3/x.pdf",
            "a sibling whose id merely STARTS with the moved one's is not under it");
        (moved with { Attachments = copy.Attachments }).Should().Be(copy, "nothing but the stored paths changes");
    }

    [Fact]
    public void TheAttachmentTargets_TellTheNewPlaceFromTheLegacyOne()
    {
        var copy = MailCopyNodeTypes.CopyPath("jroe", "outlook", "g1", "m1");
        var legacy = $"jroe/_MailCopy/outlook/{copy.Split('/')[^1]}";

        MailAttachments.IsCopyTarget("jroe", copy).Should().BeTrue();
        MailAttachments.IsCopyTarget("jroe", legacy).Should().BeFalse("nothing is written to the legacy place any more");
        MailAttachments.IsLegacyCopyTarget("jroe", legacy).Should().BeTrue("the move reads and empties exactly the legacy copy nodes");
        MailAttachments.IsLegacyCopyTarget("jroe", copy).Should().BeFalse();
        MailAttachments.IsLegacyCopyTarget("jroe", "jroe/Notes/x").Should().BeFalse("never another node of the home partition");
        MailAttachments.IsLegacyCopyTarget("jroe", "mallory/_MailCopy/outlook/x").Should().BeFalse("never another person's");
    }

    [Fact(Timeout = 120_000)]
    public async Task SwitchingOn_MakesTheOwnersOwnPartition_WhoseOneGrantIsTheOwnersViewerGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        Task<MailCopySource> created;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            created = store.Create(owner, new MailCopySource { Provider = "outlook", Generation = "g1" }).Timeout(Bound).Await(ct);
        await created;

        var partition = MailCopyNodeTypes.NamespaceFor(owner);
        (await Present(partition, ct)).NodeType.Should().Be(MailCopyNodeTypes.PartitionNodeType);
        await Present(MailCopyNodeTypes.SourcePath(owner, "outlook"), ct);
        (await Listed(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), ct)).Should().BeNull("nothing is filed in the home partition");

        using (AsSystem())
        {
            var grants = await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{partition} scope:subtree nodeType:AccessAssignment"))
                .Take(1).Timeout(Bound).Await(ct);
            var only = grants.Items.Should().ContainSingle("the owner's grant is the partition's one grant").Subject;
            var assignment = only.ContentAs<AccessAssignment>(Mesh.JsonSerializerOptions)!;
            assignment.AccessObject.Should().Be(owner);
            assignment.Roles.Should().ContainSingle().Which.Role.Should().Be(Role.Viewer.Id);
        }

        (await Mesh.CheckPermissionOutcome(partition, owner, Permission.Read).Take(1).Timeout(Bound).Await(ct))
            .IsGranted.Should().BeTrue("the owner reads their copies");
        (await Mesh.CheckPermissionOutcome(partition, "mallory", Permission.Read).Take(1).Timeout(Bound).Await(ct))
            .IsGranted.Should().BeFalse("nobody else does");

        // 🚨 A grant on the person's HOME partition does not reach the copies any more.
        await Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(new MeshNode("homehelper_Access", $"{owner}/_Access")
        {
            NodeType = AccessAssignmentNodeType.NodeType,
            Name = "homehelper Access",
            MainNode = owner,
            State = MeshNodeState.Active,
            Content = new AccessAssignment
            {
                AccessObject = "homehelper",
                DisplayName = "homehelper",
                Roles = [new RoleAssignment { Role = Role.Editor.Id, Denied = false }],
            },
        }).Timeout(Bound).Await(ct);
        (await Mesh.CheckPermissionOutcome(owner, "homehelper", Permission.Read).Take(1).Timeout(Bound).Await(ct))
            .IsGranted.Should().BeTrue("the control: the grant works on the home partition");
        (await Mesh.CheckPermissionOutcome(partition, "homehelper", Permission.Read).Take(1).Timeout(Bound).Await(ct))
            .IsGranted.Should().BeFalse("and stops there — the mail is in a partition of its own");

        // Switching on again — another click, another replica — changes nothing.
        using (access.SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            created = store.Create(owner, new MailCopySource { Provider = "outlook", Generation = "g2" }).Timeout(Bound).Await(ct);
        (await created).Generation.Should().Be("g1", "the first switch-on is kept");
    }

    [Fact(Timeout = 120_000)]
    public async Task ALegacyCopy_IsMoved_WithItsCursorCopiesAndFiles_AndTheLegacyOneIsGone()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var files = new RecordingAttachments();
        var store = new MeshMailCopyStore(() => Mesh, attachments: files);
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var legacySource = new MailCopySource
        {
            Provider = "outlook", Generation = "g1", Cursor = "https://graph.example/delta?x=1", Backfilled = true,
            // A lease that LAPSED (its holder crashed): the move takes it over.
            LeaseHolder = "old-replica", LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        var id = MailCopyNodeTypes.IdFor("outlook", "g1", "m1");
        var legacyCopyPath = $"{MailCopyNodeTypes.LegacySourcePath(owner, "outlook")}/{id}";
        var withFile = new MailMessageCopy
        {
            Provider = "outlook", Generation = "g1", MessageId = "m1", Subject = "The deck", HasAttachments = true,
            Attachments = [new MailAttachmentCopy { AttachmentId = "a1", Name = "deck.pptx", StoredPath = $"{legacyCopyPath}/attachments/f1/deck.pptx" }],
        };
        var plainId = MailCopyNodeTypes.IdFor("outlook", "g1", "m2");
        using (AsSystem())
        {
            await mesh.CreateNode(new MeshNode("outlook", MailCopyNodeTypes.LegacyNamespaceFor(owner))
            { NodeType = MailCopyNodeTypes.SourceNodeType, State = MeshNodeState.Active, Content = legacySource }).Timeout(Bound).Await(ct);
            await mesh.CreateNode(new MeshNode(id, MailCopyNodeTypes.LegacySourcePath(owner, "outlook"))
            { Name = "The deck", NodeType = MailCopyNodeTypes.MessageNodeType, State = MeshNodeState.Active, Content = withFile }).Timeout(Bound).Await(ct);
            await mesh.CreateNode(new MeshNode(plainId, MailCopyNodeTypes.LegacySourcePath(owner, "outlook"))
            {
                Name = "Plain", NodeType = MailCopyNodeTypes.MessageNodeType, State = MeshNodeState.Active,
                Content = new MailMessageCopy { Provider = "outlook", Generation = "g1", MessageId = "m2", Subject = "Plain", HasAttachments = false },
            }).Timeout(Bound).Await(ct);
        }

        Task<int> move;
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            move = store.MoveLegacy(owner, "outlook").Timeout(Bound).Await(ct);
        (await move).Should().Be(2);

        var source = (await Present(MailCopyNodeTypes.SourcePath(owner, "outlook"), ct)).ContentAs<MailCopySource>(Mesh.JsonSerializerOptions)!;
        source.Generation.Should().Be("g1", "the moved copies stay current");
        source.Cursor.Should().Be(legacySource.Cursor, "the next sync resumes where the legacy one stopped — nothing is downloaded again");
        source.Backfilled.Should().BeTrue();
        source.LeaseHolder.Should().BeNull("the move released the destination when it was done");

        var copyPath = MailCopyNodeTypes.CopyPath(owner, "outlook", "g1", "m1");
        var moved = (await Present(copyPath, ct)).ContentAs<MailMessageCopy>(Mesh.JsonSerializerOptions)!;
        moved.Subject.Should().Be("The deck");
        moved.Attachments!.Single().StoredPath.Should().Be($"{copyPath}/attachments/f1/deck.pptx");
        await Present(MailCopyNodeTypes.CopyPath(owner, "outlook", "g1", "m2"), ct);

        files.Calls.Should().Equal(
            [$"copy {legacyCopyPath} -> {copyPath}", $"delete {legacyCopyPath}"],
            "only the copy with files has files moved, copied before the legacy ones are deleted");

        await Absent(legacyCopyPath, ct);
        await Absent($"{MailCopyNodeTypes.LegacySourcePath(owner, "outlook")}/{plainId}", ct);
        await Absent(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), ct);

        // Repeated: nothing left to move.
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            move = store.MoveLegacy(owner, "outlook").Timeout(Bound).Await(ct);
        (await move).Should().Be(0);
    }

    [Fact(Timeout = 120_000)]
    public async Task AMoveWhoseFilesCannotBeCopied_KeepsTheLegacyCopy_AndTheLegacySource()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var files = new RecordingAttachments { FailCopy = true };
        var store = new MeshMailCopyStore(() => Mesh, attachments: files);
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var id = MailCopyNodeTypes.IdFor("outlook", "g1", "m1");
        var legacyCopyPath = $"{MailCopyNodeTypes.LegacySourcePath(owner, "outlook")}/{id}";
        using (AsSystem())
        {
            await mesh.CreateNode(new MeshNode("outlook", MailCopyNodeTypes.LegacyNamespaceFor(owner))
            { NodeType = MailCopyNodeTypes.SourceNodeType, State = MeshNodeState.Active, Content = new MailCopySource { Provider = "outlook", Generation = "g1" } }).Timeout(Bound).Await(ct);
            await mesh.CreateNode(new MeshNode(id, MailCopyNodeTypes.LegacySourcePath(owner, "outlook"))
            {
                NodeType = MailCopyNodeTypes.MessageNodeType, State = MeshNodeState.Active,
                Content = new MailMessageCopy { Provider = "outlook", Generation = "g1", MessageId = "m1", HasAttachments = true },
            }).Timeout(Bound).Await(ct);
        }

        Task<int> move;
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            move = store.MoveLegacy(owner, "outlook").Timeout(Bound).Await(ct);
        (await Record.ExceptionAsync(() => move)).Should().NotBeNull("a move that could not copy the files fails");

        files.Calls.Should().NotContain(c => c.StartsWith("delete"), "no legacy file is deleted that was not copied");
        await Present(legacyCopyPath, ct);
        await Present(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), ct);
        var held = (await Present(MailCopyNodeTypes.SourcePath(owner, "outlook"), ct)).ContentAs<MailCopySource>(Mesh.JsonSerializerOptions)!;
        held.Backfilled.Should().BeFalse("a half-moved copy is never served from");
        held.LeaseHolder.Should().BeNull("the failed attempt released its lease, so the next one need not wait it out");
        (await Present(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), ct)).ContentAs<MailCopySource>(Mesh.JsonSerializerOptions)!
            .LeaseHolder.Should().BeNull("and its fence on the legacy copy");

        // The next attempt — files reachable again — finishes the move at once.
        var retry = new MeshMailCopyStore(() => Mesh, attachments: new RecordingAttachments());
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            move = retry.MoveLegacy(owner, "outlook").Timeout(Bound).Await(ct);
        (await move).Should().Be(1);
        await Absent(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task ALegacyCopyLeasedByALiveSync_IsNotMoved_AndNothingIsCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh, attachments: new RecordingAttachments());
        using (AsSystem())
            await Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(new MeshNode("outlook", MailCopyNodeTypes.LegacyNamespaceFor(owner))
            {
                NodeType = MailCopyNodeTypes.SourceNodeType, State = MeshNodeState.Active,
                // A replica still running the previous image is syncing the legacy place right now.
                Content = new MailCopySource { Provider = "outlook", Generation = "g1", LeaseHolder = "old-replica", LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(5) },
            }).Timeout(Bound).Await(ct);

        Task<int> move;
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            move = store.MoveLegacy(owner, "outlook").Timeout(Bound).Await(ct);
        (await Record.ExceptionAsync(() => move)).Should().BeOfType<InvalidOperationException>("the fence is taken only when no live lease holds the legacy copy");

        (await Listed(MailCopyNodeTypes.SourcePath(owner, "outlook"), ct)).Should().BeNull("nothing is created before the fence is taken");
        (await Present(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), ct)).ContentAs<MailCopySource>(Mesh.JsonSerializerOptions)!
            .LeaseHolder.Should().Be("old-replica", "the running sync keeps its lease");
    }

    [Fact(Timeout = 120_000)]
    public async Task SwitchingOnAgain_OverALegacyCopy_KeepsItsGenerationAndCursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh, attachments: new RecordingAttachments());
        using (AsSystem())
            await Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(new MeshNode("outlook", MailCopyNodeTypes.LegacyNamespaceFor(owner))
            {
                NodeType = MailCopyNodeTypes.SourceNodeType, State = MeshNodeState.Active,
                Content = new MailCopySource { Provider = "outlook", Generation = "legacy-gen", Cursor = "https://graph.example/delta?x=7", Backfilled = true },
            }).Timeout(Bound).Await(ct);

        Task<MailCopySource> created;
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            created = store.Create(owner, new MailCopySource { Provider = "outlook", Generation = "fresh-gen" }).Timeout(Bound).Await(ct);

        (await created).Generation.Should().Be("legacy-gen", "the legacy copy is moved before a fresh one could be created");
        var source = (await Present(MailCopyNodeTypes.SourcePath(owner, "outlook"), ct)).ContentAs<MailCopySource>(Mesh.JsonSerializerOptions)!;
        source.Cursor.Should().Be("https://graph.example/delta?x=7");
        source.Backfilled.Should().BeTrue("released with the legacy copy's real state once moved");
        await Absent(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task APartitionOfThatNameWhichIsNotACopyPartition_IsNeverWrittenTo()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        // Someone made a Space called "{owner}_MailCopy": its admins would read the mail.
        await SeedTopLevel(MeshNode.FromPath(MailCopyNodeTypes.NamespaceFor(owner)) with
        { Name = "Look-alike", NodeType = "Markdown", State = MeshNodeState.Active }).WaitAsync(ct);
        var store = new MeshMailCopyStore(() => Mesh);

        Task<MailCopySource> created;
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            created = store.Create(owner, new MailCopySource { Provider = "outlook", Generation = "g1" }).Timeout(Bound).Await(ct);

        (await Record.ExceptionAsync(() => created)).Should().BeOfType<InvalidOperationException>("the switch fails closed");
        (await Listed(MailCopyNodeTypes.SourcePath(owner, "outlook"), ct)).Should().BeNull("no mail is written into it");
        (await Listed($"{MailCopyNodeTypes.NamespaceFor(owner)}/_Access/{owner}_Access", ct)).Should().BeNull("and no grant for the owner on it");
    }

    /// <summary>
    /// The account switch reads either place: a copy not yet moved shows as ON and is switched off through the public
    /// account API — moved first by the store's Disable, then deleted with its files. (Its preliminary account check
    /// read only the copy partition, so a legacy-only copy read as off and its Disable deleted nothing.)
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ALegacyOnlyCopy_IsOnInTheAccountSwitch_AndSwitchingItOffDeletesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var files = new RecordingAttachments();
        var store = new MeshMailCopyStore(() => Mesh, attachments: files);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var feed = new OneAccountFeed();
        var account = new MailFeedAccountCopy(new MailCopyEngine(feed, store, access: access, attachments: files), feed);
        var id = MailCopyNodeTypes.IdFor("outlook", "g1", "m1");
        using (AsSystem())
        {
            var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
            await mesh.CreateNode(new MeshNode("outlook", MailCopyNodeTypes.LegacyNamespaceFor(owner))
            {
                NodeType = MailCopyNodeTypes.SourceNodeType, State = MeshNodeState.Active,
                Content = new MailCopySource { Provider = "outlook", Generation = "g1", MailboxId = "acct", Backfilled = true },
            }).Timeout(Bound).Await(ct);
            await mesh.CreateNode(new MeshNode(id, MailCopyNodeTypes.LegacySourcePath(owner, "outlook"))
            {
                NodeType = MailCopyNodeTypes.MessageNodeType, State = MeshNodeState.Active,
                Content = new MailMessageCopy { Provider = "outlook", Generation = "g1", MessageId = "m1", HasAttachments = true },
            }).Timeout(Bound).Await(ct);
        }

        Task<ImmutableList<MailCopyAccount>> offered;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            offered = account.Accounts(owner).Timeout(Bound).Await(ct);
        (await offered).Should().ContainSingle().Which.IsOn.Should().BeTrue("the legacy copy is still on");

        Task<bool> off;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            off = account.Disable(owner, "acct").Timeout(Bound).Await(ct);
        (await off).Should().BeTrue("the copy was there to switch off");

        await Absent(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), ct);
        await Absent($"{MailCopyNodeTypes.LegacySourcePath(owner, "outlook")}/{id}", ct);
        await Absent(MailCopyNodeTypes.SourcePath(owner, "outlook"), ct);
        files.Calls.Should().Contain(c => c.StartsWith("delete ")).And.Contain(c => c.StartsWith("copy "),
            "its files were moved and then went with the switch-off");
    }

    /// <summary>
    /// Overlapping moves on ONE store — a switch-on, a switch-off's move and a background sync at once — never both pass
    /// the fence: each attempt is its own lease holder. Whatever the interleaving, the copy ends up moved exactly once,
    /// released with its real state, and the legacy one is gone.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task OverlappingMovesOnOneStore_MoveTheCopyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh, attachments: new RecordingAttachments());
        using (AsSystem())
        {
            var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
            await mesh.CreateNode(new MeshNode("outlook", MailCopyNodeTypes.LegacyNamespaceFor(owner))
            {
                NodeType = MailCopyNodeTypes.SourceNodeType, State = MeshNodeState.Active,
                Content = new MailCopySource { Provider = "outlook", Generation = "g1", Cursor = "https://graph.example/delta?x=3", Backfilled = true },
            }).Timeout(Bound).Await(ct);
            foreach (var m in new[] { "m1", "m2", "m3" })
                await mesh.CreateNode(new MeshNode(MailCopyNodeTypes.IdFor("outlook", "g1", m), MailCopyNodeTypes.LegacySourcePath(owner, "outlook"))
                {
                    NodeType = MailCopyNodeTypes.MessageNodeType, State = MeshNodeState.Active,
                    Content = new MailMessageCopy { Provider = "outlook", Generation = "g1", MessageId = m, Subject = m },
                }).Timeout(Bound).Await(ct);
        }

        var attempts = new List<Task<int>>();
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            for (var i = 0; i < 3; i++)
                attempts.Add(store.MoveLegacy(owner, "outlook").Timeout(Bound).Await(ct));
        var outcomes = new List<int?>();
        foreach (var attempt in attempts)
            outcomes.Add(await Record.ExceptionAsync(() => attempt) is null ? await attempt : null);

        outcomes.Should().Contain(n => n != null, "one attempt moves the copy");
        outcomes.Sum(n => n ?? 0).Should().Be(3, "each copy is moved by exactly one attempt");
        await Absent(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), ct);
        var source = (await Present(MailCopyNodeTypes.SourcePath(owner, "outlook"), ct)).ContentAs<MailCopySource>(Mesh.JsonSerializerOptions)!;
        source.Backfilled.Should().BeTrue("released with the legacy copy's real state — never deleted as 'switched off' by a losing attempt");
        source.LeaseHolder.Should().BeNull();
        source.Cursor.Should().Be("https://graph.example/delta?x=3");
        foreach (var m in new[] { "m1", "m2", "m3" })
            await Present(MailCopyNodeTypes.CopyPath(owner, "outlook", "g1", m), ct);
    }

    private sealed class OneAccountFeed : IMailCopyFeed
    {
        public string Provider => "outlook";
        public IObservable<string> AccountId(string owner) => Observable.Return("acct");
        public IObservable<ImmutableList<(string AccountId, string Label)>> Accounts(string owner) => Observable.Return(ImmutableList.Create(("acct", "Mail")));
        public IObservable<MailFeedStep> Step(string owner, string? cursor, DateTimeOffset windowStart) => Observable.Empty<MailFeedStep>();
        public IObservable<IMailAttachmentSource> Attachments(string owner) => Observable.Empty<IMailAttachmentSource>();
    }

    private sealed class RecordingAttachments : IMailAttachmentStore
    {
        public bool FailCopy { get; init; }
        private ImmutableList<string> calls = [];
        public IReadOnlyList<string> Calls => calls;

        public IObservable<string> Save(string owner, string copyPath, string relativePath, byte[] bytes)
            => Observable.Return(MailAttachments.StoredPathOf(copyPath, relativePath));

        public IObservable<Unit> DeleteAll(string owner, string copyPath)
            => Observable.Return(Unit.Default);

        public IObservable<int> CopyLegacy(string owner, string legacyCopyPath, string copyPath)
            => FailCopy
                ? Observable.Throw<int>(new InvalidOperationException("the file share is not reachable"))
                : Observable.Defer(() =>
                {
                    ImmutableInterlocked.Update(ref calls, c => c.Add($"copy {legacyCopyPath} -> {copyPath}"));
                    return Observable.Return(1);
                });

        public IObservable<Unit> DeleteLegacy(string owner, string legacyCopyPath)
            => Observable.Defer(() =>
            {
                ImmutableInterlocked.Update(ref calls, c => c.Add($"delete {legacyCopyPath}"));
                return Observable.Return(Unit.Default);
            });
    }
}
