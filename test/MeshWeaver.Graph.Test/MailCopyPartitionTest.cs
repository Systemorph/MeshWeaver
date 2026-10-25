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

    // Whether the listing has the node, read as System — one query, composed at subscribe.
    private IObservable<bool> IsListed(string path)
        => Observable.Defer(() =>
        {
            using var _ = AsSystem();
            return Mesh.ServiceProvider.GetRequiredService<IMeshService>()
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}") with { Limit = 1 })
                .Take(1)
                .Select(change => change.Items.Any(n => string.Equals(n.Path, path, StringComparison.Ordinal)));
        });

    // The listing re-read until it says WANT — one bounded wait over the whole condition, never a sleep loop.
    private IObservable<bool> Until(string path, bool want)
        => Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(-1L)
            .Select(_ => IsListed(path))
            .Concat()
            .Where(listed => listed == want)
            .Take(1)
            .Timeout(Bound);

    // A node the test expects to EXIST: found through the listing (waiting while the index catches up), then read
    // from its stream — never a first query frame taken as the node's post-write state.
    private Task<MeshNode> Present(string path, CancellationToken ct)
        => Until(path, want: true)
            .SelectMany(_ => Observable.Defer(() =>
            {
                using var _ = AsSystem();
                return Mesh.GetMeshNodeStream(path).Take(1);
            }))
            .Take(1)
            .Timeout(Bound)
            .Await(ct);

    // A node the test expects to be GONE: the listing re-read until it no longer has it, within the bound.
    private Task Absent(string path, CancellationToken ct) => Until(path, want: false).Await(ct);

    // A source node's content as its stream has it, re-read until PREDICATE holds, within the bound.
    private Task<MailCopySource> SourceWhere(string path, Func<MailCopySource, bool> predicate, CancellationToken ct)
        => Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(-1L)
            .Select(_ => Observable.Defer(() =>
            {
                using var _ = AsSystem();
                return Mesh.GetMeshNodeStream(path).Take(1);
            }).Select(node => node.ContentAs<MailCopySource>(Mesh.JsonSerializerOptions)!))
            .Concat()
            .Where(predicate)
            .Take(1)
            .Timeout(Bound)
            .Await(ct);

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
    public async Task ACopyNotYetRetired_IsStillListed_ForTheOwnerAndForTheKeeper()
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
            .And.Contain(new MailCopySourceRef(owner, "gmail"), "and so is one still at the legacy place — resuming it is what retires it");
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
    /// account API — deleted by the store's Disable with its files. (Its preliminary account check
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
        files.Calls.Should().ContainSingle(c => c.StartsWith("delete "), "its files went with the switch-off, from the legacy place");
    }

    // A legacy copy as it sat in the home partition before 2026-10-25: its source and messages, as System.
    private async Task SeedLegacy(string owner, MailCopySource source, IEnumerable<MailMessageCopy> copies, CancellationToken ct)
    {
        using var _ = AsSystem();
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(new MeshNode(source.Provider, MailCopyNodeTypes.LegacyNamespaceFor(owner))
        { NodeType = MailCopyNodeTypes.SourceNodeType, State = MeshNodeState.Active, Content = source }).Timeout(Bound).Await(ct);
        foreach (var copy in copies)
            await mesh.CreateNode(new MeshNode(MailCopyNodeTypes.IdFor(copy.Provider, copy.Generation, copy.MessageId), MailCopyNodeTypes.LegacySourcePath(owner, source.Provider))
            { Name = copy.Subject ?? "x", NodeType = MailCopyNodeTypes.MessageNodeType, State = MeshNodeState.Active, Content = copy }).Timeout(Bound).Await(ct);
    }

    private static string LegacyCopyPath(string owner, string generation, string messageId)
        => $"{MailCopyNodeTypes.LegacySourcePath(owner, "outlook")}/{MailCopyNodeTypes.IdFor("outlook", generation, messageId)}";

    private Task<T> AsOwner<T>(string owner, Func<IObservable<T>> call, CancellationToken ct)
    {
        using var _ = Mesh.ServiceProvider.GetRequiredService<AccessService>().SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner });
        return call().Timeout(Bound).Await(ct);
    }

    /// <summary>
    /// A legacy copy is RETIRED, never moved (owner, 2026-10-25: delete and download fresh): every legacy copy goes the
    /// switch-off way — its files first, then its node — the legacy source last, and a FRESH copy is switched on in the
    /// copy partition with the same window and account, a new generation, no cursor and not backfilled, which the next
    /// sync downloads. Nothing of the mail stays in the home partition.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ALegacyCopy_IsRetired_WithItsFiles_AndAFreshCopyIsSwitchedOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var files = new RecordingAttachments();
        var store = new MeshMailCopyStore(() => Mesh, attachments: files);
        var window = DateTimeOffset.UtcNow.AddDays(-90);
        var legacy = new MailCopySource
        {
            Provider = "outlook", Generation = "g1", Cursor = "https://graph.example/delta?x=1", Backfilled = true,
            MailboxId = "acct", WindowStart = window, EnabledAt = window.AddDays(90),
            // A lease that LAPSED (its holder crashed): the retirement takes it over.
            LeaseHolder = "old-replica", LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        await SeedLegacy(owner, legacy,
        [
            new MailMessageCopy { Provider = "outlook", Generation = "g1", MessageId = "m1", Subject = "The deck", HasAttachments = true,
                Attachments = [new MailAttachmentCopy { AttachmentId = "a1", Name = "deck.pptx", StoredPath = $"{LegacyCopyPath(owner, "g1", "m1")}/attachments/f1/deck.pptx" }] },
            new MailMessageCopy { Provider = "outlook", Generation = "g1", MessageId = "m2", Subject = "Plain", HasAttachments = false },
        ], ct);

        (await AsOwner(owner, () => store.RetireLegacy(owner, "outlook"), ct)).Should().BeTrue();

        files.Calls.Should().Equal([$"delete {LegacyCopyPath(owner, "g1", "m1")}"], "only the copy that may hold files has files to delete");
        await Absent(LegacyCopyPath(owner, "g1", "m1"), ct);
        await Absent(LegacyCopyPath(owner, "g1", "m2"), ct);
        await Absent(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), ct);

        var fresh = await SourceWhere(MailCopyNodeTypes.SourcePath(owner, "outlook"), _ => true, ct);
        fresh.Generation.Should().NotBe("g1", "the copy is downloaded fresh, never carried over");
        fresh.Cursor.Should().BeNull();
        fresh.Backfilled.Should().BeFalse("the inbox reads live until the fresh backfill is done");
        fresh.LeaseHolder.Should().BeNull();
        fresh.MailboxId.Should().Be("acct", "the account binding holds");
        fresh.WindowStart.Should().Be(window, "the backfill covers the same period");
        fresh.EnabledAt.Should().Be(legacy.EnabledAt);
        fresh.Projection.Should().Be(MailCopySource.CurrentProjection);
        (await Listed(MailCopyNodeTypes.CopyPath(owner, "outlook", "g1", "m1"), ct)).Should().BeNull("no copy is carried over");

        (await AsOwner(owner, () => store.RetireLegacy(owner, "outlook"), ct)).Should().BeFalse("nothing is left to retire");
    }

    [Fact(Timeout = 120_000)]
    public async Task ARetirementWhoseFilesCannotBeDeleted_KeepsTheLegacyCopy_AndSwitchesNothingOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh, attachments: new RecordingAttachments { FailDelete = true });
        await SeedLegacy(owner, new MailCopySource { Provider = "outlook", Generation = "g1", Backfilled = true },
            [new MailMessageCopy { Provider = "outlook", Generation = "g1", MessageId = "m1", HasAttachments = true }], ct);

        (await Record.ExceptionAsync(() => AsOwner(owner, () => store.RetireLegacy(owner, "outlook"), ct)))
            .Should().NotBeNull("a retirement that could not delete the files fails");

        await Present(LegacyCopyPath(owner, "g1", "m1"), ct);   // the node keeps the only handle on its files
        var legacy = await SourceWhere(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), s => s.LeaseHolder is null, ct);
        legacy.Backfilled.Should().BeFalse("a fenced legacy copy is never served from again — readers of either image honor Backfilled");
        (await Listed(MailCopyNodeTypes.SourcePath(owner, "outlook"), ct)).Should().BeNull("nothing is switched on until the legacy copy is gone");

        // The next attempt — files reachable again — finishes at once: the failed one released its fence.
        var retry = new MeshMailCopyStore(() => Mesh, attachments: new RecordingAttachments());
        (await AsOwner(owner, () => retry.RetireLegacy(owner, "outlook"), ct)).Should().BeTrue();
        await Absent(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), ct);
        await Present(MailCopyNodeTypes.SourcePath(owner, "outlook"), ct);
    }

    [Fact(Timeout = 120_000)]
    public async Task ALegacyCopyLeasedByALiveSync_IsNotRetired_AndNothingIsCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh, attachments: new RecordingAttachments());
        // A replica still running the previous image is syncing the legacy place right now.
        await SeedLegacy(owner, new MailCopySource { Provider = "outlook", Generation = "g1", LeaseHolder = "old-replica", LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(5) },
            [new MailMessageCopy { Provider = "outlook", Generation = "g1", MessageId = "m1" }], ct);

        (await Record.ExceptionAsync(() => AsOwner(owner, () => store.RetireLegacy(owner, "outlook"), ct)))
            .Should().BeOfType<InvalidOperationException>("the fence is taken only when no live lease holds the legacy copy");

        await Present(LegacyCopyPath(owner, "g1", "m1"), ct);
        (await Listed(MailCopyNodeTypes.SourcePath(owner, "outlook"), ct)).Should().BeNull("nothing is created before the fence is taken");
        (await SourceWhere(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), _ => true, ct)).LeaseHolder
            .Should().Be("old-replica", "the running sync keeps its lease");
    }

    [Fact(Timeout = 120_000)]
    public async Task SwitchingOnAgain_OverALegacyCopy_RetiresIt_AndKeepsOneFreshCopy()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh, attachments: new RecordingAttachments());
        await SeedLegacy(owner, new MailCopySource { Provider = "outlook", Generation = "legacy-gen", Cursor = "https://graph.example/delta?x=7", Backfilled = true },
            [new MailMessageCopy { Provider = "outlook", Generation = "legacy-gen", MessageId = "m1" }], ct);

        var created = await AsOwner(owner, () => store.Create(owner, new MailCopySource { Provider = "outlook", Generation = "fresh-gen" }), ct);

        await Absent(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), ct);
        await Absent(LegacyCopyPath(owner, "legacy-gen", "m1"), ct);
        var source = await SourceWhere(MailCopyNodeTypes.SourcePath(owner, "outlook"), _ => true, ct);
        created.Generation.Should().Be(source.Generation, "the switch-on answers the one copy that is on");
        source.Generation.Should().NotBe("legacy-gen");
        source.Cursor.Should().BeNull();
        source.Backfilled.Should().BeFalse();
    }

    /// <summary>
    /// A switch-off by a replica on the PREVIOUS image (which deletes the legacy source directly) lands while the
    /// retirement deletes files: the retirement's own delete then removes nothing, and no fresh copy is switched on —
    /// the person's switch-off stands.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnOldImageSwitchOffDuringTheRetirement_SwitchesNothingOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var files = new RecordingAttachments
        {
            OnDelete = () => Observable.Defer(() =>
            {
                using var _ = AsSystem();
                return mesh.DeleteNode(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"));
            }).Select(_ => Unit.Default),
        };
        var store = new MeshMailCopyStore(() => Mesh, attachments: files);
        await SeedLegacy(owner, new MailCopySource { Provider = "outlook", Generation = "g1", Backfilled = true },
            [new MailMessageCopy { Provider = "outlook", Generation = "g1", MessageId = "m1", HasAttachments = true }], ct);

        (await AsOwner(owner, () => store.RetireLegacy(owner, "outlook"), ct)).Should().BeFalse("the legacy source was gone when this attempt came to delete it");

        await Absent(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), ct);
        (await Listed(MailCopyNodeTypes.SourcePath(owner, "outlook"), ct)).Should().BeNull("a switched-off copy is not switched back on");
    }

    /// <summary>
    /// An old-image replica switches the copy off and on again while the retirement runs — the legacy source is REPLACED
    /// by a new generation. The retirement's confirmation at the owning hub refuses: the new legacy source is not deleted,
    /// and nothing is switched on from the stale one (the next sync retires the new one).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ALegacySourceReplacedDuringTheRetirement_IsNotDeleted_AndNothingIsSwitchedOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var files = new RecordingAttachments
        {
            OnDelete = () => Observable.Defer(() =>
            {
                using var _ = AsSystem();
                return mesh.DeleteNode(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"))
                    .SelectMany(_ => mesh.CreateNode(new MeshNode("outlook", MailCopyNodeTypes.LegacyNamespaceFor(owner))
                    {
                        NodeType = MailCopyNodeTypes.SourceNodeType, State = MeshNodeState.Active,
                        Content = new MailCopySource { Provider = "outlook", Generation = "g2", MailboxId = "other" },
                    }));
            }).Select(_ => Unit.Default),
        };
        var store = new MeshMailCopyStore(() => Mesh, attachments: files);
        await SeedLegacy(owner, new MailCopySource { Provider = "outlook", Generation = "g1", Backfilled = true, MailboxId = "acct" },
            [new MailMessageCopy { Provider = "outlook", Generation = "g1", MessageId = "m1", HasAttachments = true }], ct);

        // Refused (the confirmation saw the replacement) or a no-op (the index did not show it yet, so the source read as
        // gone): either way nothing of the replacing source is touched and nothing is switched on.
        var failure = await Record.ExceptionAsync(() => AsOwner(owner, () => store.RetireLegacy(owner, "outlook"), ct));
        if (failure is not null)
            failure.Should().BeOfType<InvalidOperationException>("the source this attempt fenced was replaced");

        (await SourceWhere(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), _ => true, ct)).Generation
            .Should().Be("g2", "the replacing source is not deleted");
        (await Listed(MailCopyNodeTypes.SourcePath(owner, "outlook"), ct)).Should().BeNull("nothing is switched on from the stale source");
    }

    /// <summary>A legacy copy whose content cannot be read is treated as HOLDING files: they are deleted before its node.</summary>
    [Fact(Timeout = 120_000)]
    public async Task AnUnreadableLegacyCopy_HasItsFilesDeleted_BeforeItsNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var files = new RecordingAttachments();
        var store = new MeshMailCopyStore(() => Mesh, attachments: files);
        await SeedLegacy(owner, new MailCopySource { Provider = "outlook", Generation = "g1" }, [], ct);
        var unreadable = $"{MailCopyNodeTypes.LegacySourcePath(owner, "outlook")}/0123456789abcdef0123456789abcdef";
        using (AsSystem())
            await Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(new MeshNode("0123456789abcdef0123456789abcdef", MailCopyNodeTypes.LegacySourcePath(owner, "outlook"))
            { NodeType = MailCopyNodeTypes.MessageNodeType, State = MeshNodeState.Active, Content = "not a copy" }).Timeout(Bound).Await(ct);

        (await AsOwner(owner, () => store.RetireLegacy(owner, "outlook"), ct)).Should().BeTrue();

        files.Calls.Should().Equal([$"delete {unreadable}"], "unknown content may hold files, so its files go first");
        await Absent(unreadable, ct);
    }

    /// <summary>
    /// Overlapping retirements on ONE store — a switch-on, a switch-off's and a background sync's at once — never both pass
    /// the fence: each attempt is its own lease holder. Whatever the interleaving, the legacy copy is gone and exactly one
    /// fresh copy is on.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task OverlappingRetirementsOnOneStore_LeaveOneFreshCopy()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh, attachments: new RecordingAttachments());
        await SeedLegacy(owner, new MailCopySource { Provider = "outlook", Generation = "g1", Backfilled = true },
            new[] { "m1", "m2", "m3" }.Select(m => new MailMessageCopy { Provider = "outlook", Generation = "g1", MessageId = m }), ct);

        var attempts = Enumerable.Range(0, 3).Select(_ => AsOwner(owner, () => store.RetireLegacy(owner, "outlook"), ct)).ToList();
        var outcomes = new List<bool?>();
        foreach (var attempt in attempts)
            outcomes.Add(await Record.ExceptionAsync(() => attempt) is null ? await attempt : null);

        outcomes.Count(o => o == true).Should().Be(1, "exactly one attempt retires the copy");
        await Absent(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), ct);
        foreach (var m in new[] { "m1", "m2", "m3" })
            await Absent(LegacyCopyPath(owner, "g1", m), ct);
        var fresh = await SourceWhere(MailCopyNodeTypes.SourcePath(owner, "outlook"), _ => true, ct);
        fresh.Generation.Should().NotBe("g1");
    }

    /// <summary>
    /// A retirement disposed after it took its fence — the sync's 30-minute bound, a caller that went away — sends no
    /// error through any Catch; it still releases its fence, so the next attempt need not wait out RetireLease.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARetirementDisposedAfterItsFence_ReleasesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh, attachments: new RecordingAttachments { HangDelete = true });
        await SeedLegacy(owner, new MailCopySource { Provider = "outlook", Generation = "g1", Backfilled = true },
            [new MailMessageCopy { Provider = "outlook", Generation = "g1", MessageId = "m1", HasAttachments = true }], ct);

        IDisposable running;
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            running = store.RetireLegacy(owner, "outlook").Subscribe(_ => { }, _ => { });
        await SourceWhere(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), s => s.LeaseHolder is not null && !s.Backfilled, ct);

        running.Dispose();

        await SourceWhere(MailCopyNodeTypes.LegacySourcePath(owner, "outlook"), s => s.LeaseHolder is null, ct);
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
        public bool FailDelete { get; init; }
        public bool HangDelete { get; init; }
        public Func<IObservable<Unit>>? OnDelete { get; init; }
        private ImmutableList<string> calls = [];
        public IReadOnlyList<string> Calls => calls;

        public IObservable<string> Save(string owner, string copyPath, string relativePath, byte[] bytes)
            => Observable.Return(MailAttachments.StoredPathOf(copyPath, relativePath));

        public IObservable<Unit> DeleteAll(string owner, string copyPath)
            => Observable.Return(Unit.Default);

        public IObservable<Unit> DeleteLegacy(string owner, string legacyCopyPath)
            => OnDelete is { } onDelete
                ? onDelete()
                : HangDelete
                ? Observable.Never<Unit>()
                : FailDelete
                ? Observable.Throw<Unit>(new InvalidOperationException("the file share is not reachable"))
                : Observable.Defer(() =>
                {
                    ImmutableInterlocked.Update(ref calls, c => c.Add($"delete {legacyCopyPath}"));
                    return Observable.Return(Unit.Default);
                });
    }
}
