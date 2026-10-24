using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text;
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
/// The mail copy's stores write as System on the owner's behalf, and they are public platform code any
/// NodeType can reach — so they authorize the CALLER before they elevate (Doc/Architecture/MailCopy). On a
/// mesh with row-level security and no blanket grant (<c>ConfigureMeshBase</c>), another signed-in person
/// can neither write nor read the owner's copy through them, while the owner — the control arm, differing
/// only in identity — can; and the attachment store touches nothing but the owner's own copy nodes.
/// </summary>
public class MailCopyAuthorityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
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

    private static MailMessageCopy Copy(string subject) =>
        new() { Provider = "outlook", Generation = "g1", MessageId = "m1", Subject = subject, CopiedAt = DateTimeOffset.UnixEpoch };

    [Fact(Timeout = 120_000)]
    public async Task AnotherPerson_CannotWriteOrReadTheOwnersCopy_WhileTheOwnerCan()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await store.Create(owner, new MailCopySource { Provider = "outlook", Generation = "g1" }).Timeout(Bound).Await(ct);

        Task<MailMessageCopy> write;
        Task<MailMessageCopy?> read;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = "mallory", Name = "mallory" }))
        {
            write = store.Upsert(owner, Copy("Forged")).Timeout(Bound).Await(ct);
            read = store.Read(owner, "outlook", "g1", "m1").Timeout(Bound).Await(ct);
        }
        var writeAsOther = await Record.ExceptionAsync(() => write);
        var readAsOther = await Record.ExceptionAsync(() => read);

        writeAsOther.Should().BeOfType<UnauthorizedAccessException>("a System write on the owner's behalf is never one another person can ask for");
        readAsOther.Should().BeOfType<UnauthorizedAccessException>("nor a read that names the owner as its viewer");
        (await store.Read(owner, "outlook", "g1", "m1").Timeout(Bound).Await(ct))
            .Should().BeNull("the refused write wrote nothing");

        using (access.SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            write = store.Upsert(owner, Copy("Mine")).Timeout(Bound).Await(ct);
        await write;
        (await store.Read(owner, "outlook", "g1", "m1").Timeout(Bound).Await(ct))!.Subject
            .Should().Be("Mine", "the control arm: the SAME write as the owner lands");
    }

    /// <summary>
    /// An empty id is an anonymous visitor, never trusted background work: it is refused like any stranger.
    /// The owner, on the same store, is the control arm.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnAnonymousCaller_IsRefused_NotTreatedAsBackgroundWork()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await store.Create(owner, new MailCopySource { Provider = "outlook", Generation = "g1" }).Timeout(Bound).Await(ct);

        Task<MailMessageCopy> write;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = "", Name = "anonymous" }))
            write = store.Upsert(owner, Copy("Forged")).Timeout(Bound).Await(ct);
        (await Record.ExceptionAsync(() => write))
            .Should().BeOfType<UnauthorizedAccessException>("an anonymous caller carries no authority over anybody's copy");
        (await store.Read(owner, "outlook", "g1", "m1").Timeout(Bound).Await(ct))
            .Should().BeNull("the refused write wrote nothing");
    }

    /// <summary>
    /// A non-empty id is not a signed-in person: the Anonymous id an unauthenticated request carries, and a
    /// VIRTUAL (logged-out) visitor that carries the owner's own id, are both refused before the owner
    /// shortcut. The owner, signed in, is the control arm in the first test.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ANamedAnonymousOrVirtualCaller_IsRefused_EvenWhenItsIdIsTheOwners()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await store.Create(owner, new MailCopySource { Provider = "outlook", Generation = "g1" }).Timeout(Bound).Await(ct);

        Task<MailMessageCopy> asVisitor;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner, IsVirtual = true }))
            asVisitor = store.Upsert(owner, Copy("Forged")).Timeout(Bound).Await(ct);
        (await Record.ExceptionAsync(() => asVisitor))
            .Should().BeOfType<UnauthorizedAccessException>("a logged-out visitor is not the owner, whatever id it carries");

        Task<MailMessageCopy> asAnonymous;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = WellKnownUsers.Anonymous, Name = WellKnownUsers.Anonymous }))
            asAnonymous = store.Upsert(WellKnownUsers.Anonymous, Copy("Forged")).Timeout(Bound).Await(ct);
        (await Record.ExceptionAsync(() => asAnonymous))
            .Should().BeOfType<UnauthorizedAccessException>("the Anonymous id is no person, so it owns no copy");

        (await store.Read(owner, "outlook", "g1", "m1").Timeout(Bound).Await(ct))
            .Should().BeNull("the refused write wrote nothing");
    }

    [Fact]
    public void AnInterruptedSavesMarker_SurvivesARewriteThatSaysNothingOrNone()
    {
        MailCopyEngine.MergedAttachmentMarker(incoming: null, prior: true)
            .Should().BeTrue("the provider saying nothing must not clear the marker removal finds the files by");
        MailCopyEngine.MergedAttachmentMarker(incoming: false, prior: true).Should().BeTrue();
        // The control: with no prior marker the incoming value is stored as it came.
        MailCopyEngine.MergedAttachmentMarker(incoming: null, prior: null).Should().BeNull();
        MailCopyEngine.MergedAttachmentMarker(incoming: false, prior: false).Should().BeFalse();
    }

    /// <summary>
    /// The caller is the one who CALLED the store: a write composed under another person's identity and
    /// subscribed after that scope ended — here under the host's admin — is still judged as that person.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheCallerIsReadWhenTheStoreIsCalled_NotWhenTheResultIsSubscribed()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await store.Create(owner, new MailCopySource { Provider = "outlook", Generation = "g1" }).Timeout(Bound).Await(ct);

        IObservable<MailMessageCopy> composed;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = "mallory", Name = "mallory" }))
            composed = store.Upsert(owner, Copy("Forged"));
        // Subscribed outside mallory's scope, where the host's identity (an admin) is ambient.
        (await Record.ExceptionAsync(() => composed.Timeout(Bound).Await(ct)))
            .Should().BeOfType<UnauthorizedAccessException>("the write was asked for by mallory, whoever subscribes it later");
        (await store.Read(owner, "outlook", "g1", "m1").Timeout(Bound).Await(ct))
            .Should().BeNull("the refused write wrote nothing");
    }

    /// <summary>
    /// The background sync DECLARES itself System at every store call, so it works however its hops left the
    /// ambient identity — here it is started under an anonymous one, which the stores refuse outright.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheBackgroundSync_DeclaresSystem_AndCopiesUnderAnAnonymousAmbientIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var engine = new MailCopyEngine(new OneMessageFeed(), store, access: access);
        var now = DateTimeOffset.UtcNow;
        await store.Create(owner, new MailCopySource
        {
            Provider = "outlook", Generation = MailCopyNodeTypes.NewGeneration(), Projection = MailCopySource.CurrentProjection,
            EnabledAt = now, WindowStart = now.AddDays(-30),
        }).Timeout(Bound).Await(ct);

        Task<MailCopySource?> sync;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = "", Name = "anonymous" }))
            sync = engine.Sync(owner).Timeout(Bound).Await(ct);
        var synced = await sync;

        synced.Should().NotBeNull("the sync ran to its commit");
        synced!.Backfilled.Should().BeTrue("the one feed step said it was done");
        (await store.Read(owner, "outlook", synced.Generation, "m1").Timeout(Bound).Await(ct))!.Subject
            .Should().Be("Hello", "the background sync wrote the copy as System on the owner's behalf");
    }

    /// <summary>
    /// Switching a copy on is judged as the person who CALLED <see cref="MailCopyEngine.Enable"/>, not as
    /// whoever later subscribes the cold result: composed under another person and subscribed under the
    /// host's admin, it is refused and creates nothing. The owner, composing the same call, is the control.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Enable_IsJudgedAsItsCaller_NotAsItsSubscriber()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var engine = new MailCopyEngine(new OneMessageFeed(), store, access: access);

        IObservable<MailCopySource> composed;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = "mallory", Name = "mallory" }))
            composed = engine.Enable(owner, 30);
        // Subscribed outside mallory's scope, where the host's identity (an admin) is ambient.
        (await Record.ExceptionAsync(() => composed.Timeout(Bound).Await(ct)))
            .Should().BeOfType<UnauthorizedAccessException>("the switch-on was asked for by mallory, whoever subscribes it later");
        (await engine.Source(owner).Timeout(Bound).Await(ct))
            .Should().BeNull("the refused switch-on created no copy");

        using (access.SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            composed = engine.Enable(owner, 30);
        (await composed.Timeout(Bound).Await(ct)).Provider
            .Should().Be("outlook", "the control arm: the SAME composition by the owner switches the copy on");
    }

    /// <summary>
    /// The per-account wrappers are judged as their CALLER too: composed under another person and subscribed
    /// under the owner, <see cref="MailFeedAccountCopy.Enable"/> and <see cref="MailFeedAccountCopy.Accounts"/>
    /// are refused — the engine call is composed at the wrapper's entry, not after the feed's account check
    /// emits under the subscriber's identity. The owner, composing the same calls, is the control; and the
    /// owner's <see cref="MailFeedAccountCopy.Disable"/>, subscribed under an anonymous identity, still
    /// switches off what the owner asked to.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheAccountWrappers_AreJudgedAsTheirCaller_NotAsTheirSubscriber()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var feed = new OneMessageFeed();
        var copy = new MailFeedAccountCopy(new MailCopyEngine(feed, store, access: access), feed);
        var mallory = new AccessContext { ObjectId = "mallory", Name = "mallory" };
        var theOwner = new AccessContext { ObjectId = owner, Name = owner };

        IObservable<bool> enable;
        IObservable<ImmutableList<MailCopyAccount>> accounts;
        using (access.SwitchAccessContext(mallory))
        {
            enable = copy.Enable(owner, "acct", 30);
            accounts = copy.Accounts(owner);
        }
        // Subscribed under the OWNER — an identity that may do both — so only the composition decides.
        Task<bool> enabled;
        Task<ImmutableList<MailCopyAccount>> listed;
        using (access.SwitchAccessContext(theOwner))
        {
            enabled = enable.Timeout(Bound).Await(ct);
            listed = accounts.Timeout(Bound).Await(ct);
        }
        (await Record.ExceptionAsync(() => enabled))
            .Should().BeOfType<UnauthorizedAccessException>("the switch-on was asked for by mallory, whoever subscribes it later");
        (await Record.ExceptionAsync(() => listed))
            .Should().BeOfType<UnauthorizedAccessException>("the copy's state was asked for by mallory, whoever subscribes it later");
        (await store.Source(owner, "outlook").Timeout(Bound).Await(ct))
            .Should().BeNull("the refused switch-on created no copy");

        using (access.SwitchAccessContext(theOwner))
        {
            enable = copy.Enable(owner, "acct", 30);
            accounts = copy.Accounts(owner);
        }
        (await enable.Timeout(Bound).Await(ct)).Should().BeTrue("the control arm: the SAME composition by the owner switches the copy on");
        (await accounts.Timeout(Bound).Await(ct)).Should().ContainSingle(a => a.AccountId == "acct" && a.IsOn);

        IObservable<bool> disable;
        using (access.SwitchAccessContext(theOwner))
            disable = copy.Disable(owner, "acct");
        Task<bool> disabled;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = "", Name = "anonymous" }))
            disabled = disable.Timeout(Bound).Await(ct);
        (await disabled).Should().BeTrue("the owner's switch-off is judged as the owner, whoever subscribes it");
        (await store.Source(owner, "outlook").Timeout(Bound).Await(ct)).Should().BeNull("the copy is gone");
    }

    /// <summary>
    /// One sync per owner per engine: a direct <see cref="MailCopyEngine.Sync"/> while another runs answers
    /// null at once instead of sharing the running sync's lease and cursor, and the gate is released when the
    /// running subscription is disposed — the next sync then runs to its commit.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Sync_IsSingleFlightPerOwner_AndReleasesTheGateOnDisposal()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var feed = new HeldFeed();
        var engine = new MailCopyEngine(feed, store, access: access);
        var now = DateTimeOffset.UtcNow;
        await store.Create(owner, new MailCopySource
        {
            Provider = "outlook", Generation = MailCopyNodeTypes.NewGeneration(), Projection = MailCopySource.CurrentProjection,
            EnabledAt = now, WindowStart = now.AddDays(-30),
        }).Timeout(Bound).Await(ct);

        var first = engine.Sync(owner).Subscribe(_ => { }, _ => { });
        (await engine.Sync(owner).Timeout(Bound).Await(ct))
            .Should().BeNull("a second sync of the same owner while one runs must not share its lease and cursor");

        first.Dispose();
        feed.Release();
        var synced = await engine.Sync(owner).Timeout(Bound).Await(ct);
        synced.Should().NotBeNull("disposing the running sync released the gate, so the next sync runs");
        synced!.Backfilled.Should().BeTrue("the released feed step said it was done");
    }

    /// <summary>
    /// The sweep deletes copies through the System-backed store, so it is never a public entry point of the
    /// engine: it runs only inside the checked <see cref="MailCopyEngine.Sync"/> and <see cref="MailCopyEngine.Kick"/>.
    /// </summary>
    [Fact]
    public void TheSweep_IsNotAPublicEntryPoint()
        => typeof(MailCopyEngine).GetMethod("Sweep", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Should().BeNull("a public Sweep would let any code delete another owner's copies as System, unchecked");

    [Fact(Timeout = 120_000)]
    public async Task TheAttachmentStore_RefusesEveryTargetButTheOwnersOwnCopyNodes()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var files = new MeshMailAttachmentStore(() => Mesh);
        var bytes = Encoding.UTF8.GetBytes("x");

        foreach (var target in new[]
                 {
                     $"{owner}/Notes",                                   // the owner's, but not a copy
                     "someoneelse/_MailCopy/outlook/0123456789abcdef0123456789abcdef", // another person's copy
                     $"{owner}/_MailCopy/outlook",                       // the source, not a copy
                     $"{owner}/_MailCopy/outlook/../../Notes",
                 })
        {
            var save = await Record.ExceptionAsync(() => files.Save(owner, target, "a/b.txt", bytes).Timeout(Bound).Await(ct));
            var delete = await Record.ExceptionAsync(() => files.DeleteAll(owner, target).Timeout(Bound).Await(ct));
            save.Should().BeOfType<ArgumentException>($"'{target}' is not one of {owner}'s copies");
            delete.Should().BeOfType<ArgumentException>($"'{target}' is not one of {owner}'s copies");
        }

        var copy = MailCopyNodeTypes.CopyPath(owner, "outlook", "g1", "m1");
        MailAttachments.IsCopyTarget(owner, copy, "0123/b.txt").Should().BeTrue("the control: the owner's own copy node is a target");
        MailAttachments.IsCopyTarget(owner, copy, "../escape.txt").Should().BeFalse("a relative path never leaves the collection");
    }

    /// <summary>
    /// 🚨 A store call composed while the host had no mesh yet, and subscribed once it has one, is refused:
    /// the work resolves the hub again when it runs, so passing it through at the call would let a stranger's
    /// write run as System with no caller check. Both stores. The owner's own write, composed once the mesh
    /// exists, is the control arm.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ACallComposedBeforeTheMeshExists_IsRefused_WhenSubscribedAfter()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        IMessageHub? portal = null;
        var store = new MeshMailCopyStore(() => portal);
        var files = new MeshMailAttachmentStore(() => portal);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await new MeshMailCopyStore(() => Mesh).Create(owner, new MailCopySource { Provider = "outlook", Generation = "g1" }).Timeout(Bound).Await(ct);

        IObservable<MailMessageCopy> write;
        IObservable<string> save;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = "mallory", Name = "mallory" }))
        {
            write = store.Upsert(owner, Copy("Forged"));
            save = files.Save(owner, MailCopyNodeTypes.CopyPath(owner, "outlook", "g1", "m1"), "a/b.txt", Encoding.UTF8.GetBytes("x"));
        }
        portal = Mesh;

        (await Record.ExceptionAsync(() => write.Timeout(Bound).Await(ct)))
            .Should().BeOfType<InvalidOperationException>("no mesh at the call is a refusal, whenever the result is subscribed");
        (await Record.ExceptionAsync(() => save.Timeout(Bound).Await(ct)))
            .Should().BeOfType<InvalidOperationException>("the attachment store fails closed the same way");
        (await store.Read(owner, "outlook", "g1", "m1").Timeout(Bound).Await(ct))
            .Should().BeNull("the refused write wrote nothing");

        using (access.SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            write = store.Upsert(owner, Copy("Mine"));
        await write.Timeout(Bound).Await(ct);
        (await store.Read(owner, "outlook", "g1", "m1").Timeout(Bound).Await(ct))!.Subject
            .Should().Be("Mine", "the control arm: composed once the mesh exists, the owner's write lands");
    }

    /// <summary>
    /// A person the owner granted access to may act on the copy — the permission check's continuation — and the
    /// work runs OFF the thread the decision was emitted on: a warm permission fold emits synchronously under its
    /// Rx gate, and mesh work inside it is the lock-order deadlock of #899.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AGrantedDelegate_MayActOnTheCopy_AndTheWorkRunsOffTheDecisionsGate()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        const string delegateId = "delegate";
        await Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(new MeshNode($"{delegateId}_Access", $"{owner}/_Access")
        {
            NodeType = AccessAssignmentNodeType.NodeType,
            Name = $"{delegateId} Access",
            MainNode = owner,
            State = MeshNodeState.Active,
            Content = new AccessAssignment
            {
                AccessObject = delegateId,
                DisplayName = delegateId,
                Roles = [new RoleAssignment { Role = Role.Editor.Id, Denied = false }],
            },
        }).Timeout(Bound).Await(ct);
        var store = new MeshMailCopyStore(() => Mesh);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await store.Create(owner, new MailCopySource { Provider = "outlook", Generation = "g1" }).Timeout(Bound).Await(ct);
        var asDelegate = new AccessContext { ObjectId = delegateId, Name = delegateId };

        Task<MailMessageCopy> write;
        using (access.SwitchAccessContext(asDelegate))
            write = store.Upsert(owner, Copy("Delegated")).Timeout(Bound).Await(ct);
        await write;
        Task<MailMessageCopy?> read;
        using (access.SwitchAccessContext(asDelegate))
            read = store.Read(owner, "outlook", "g1", "m1").Timeout(Bound).Await(ct);
        (await read)!.Subject.Should().Be("Delegated", "the granted delegate's write landed and its read answers");

        // The decision is warm now (the calls above folded it). Subscribed on THIS thread, a continuation that
        // stayed on the emitting thread would run here, inside the fold's gate.
        var subscriber = Environment.CurrentManagedThreadId;
        var worker = -1;
        IObservable<int> gated;
        using (access.SwitchAccessContext(asDelegate))
            gated = MailCopyAuthority.Gated(Mesh, typeof(MailCopyAuthorityTest).Assembly, "test", owner, Permission.Read,
                () => Observable.Defer(() =>
                {
                    worker = Environment.CurrentManagedThreadId;
                    return Observable.Return(1);
                }));
        (await gated.Timeout(Bound).Await(ct)).Should().Be(1);
        worker.Should().NotBe(subscriber, "the work runs after the decision has left the evaluator's gate");
    }

    [Fact]
    public void ATombstoneThatOwesACleanup_IsCleanedByTheAttemptThatReplacesIt()
    {
        var at = DateTimeOffset.UnixEpoch.AddDays(1);
        var engine = new MailCopyEngine(new NoFeed(), new MeshMailCopyStore(() => null), attachments: new MeshMailAttachmentStore(() => null));
        var tombstone = engine.Tombstone("g1", "m1", at.AddMinutes(-1));

        MailAttachments.MayHoldFiles(tombstone).Should().BeTrue("a tombstone keeps the marker until the node is deleted");
        // A repeat after a failed cleanup: Read never answers a tombstone, so there is no prior.
        MailCopyEngine.CleansAfterTombstone(prior: null, at, replaced: tombstone)
            .Should().BeTrue("the cleanup the earlier attempt owed is done now, not skipped");

        // The control: a copy cleaned before the write, replaced exactly as read, is not cleaned twice.
        var cleaned = new MailMessageCopy
        {
            Provider = "outlook", Generation = "g1", MessageId = "m1", CopiedAt = at.AddMinutes(-5), HasAttachments = true,
            Attachments = [new MailAttachmentCopy { AttachmentId = "a1", Kind = "file", StoredPath = "x" }],
        };
        MailCopyEngine.CleansAfterTombstone(cleaned, at, cleaned with { Attachments = [.. cleaned.Attachments!] })
            .Should().BeFalse("what the cleanup before the write already emptied");
        MailCopyEngine.CleansAfterTombstone(cleaned, at, cleaned with { Attachments = ImmutableList<MailAttachmentCopy>.Empty })
            .Should().BeTrue("a replaced copy that differs from the one cleaned is cleaned again");
    }

    private sealed class OneMessageFeed : IMailCopyFeed
    {
        public string Provider => "outlook";
        public IObservable<string> AccountId(string owner) => Observable.Return("acct");
        public IObservable<ImmutableList<(string AccountId, string Label)>> Accounts(string owner) => Observable.Return(ImmutableList.Create(("acct", "Mail")));
        public IObservable<MailFeedStep> Step(string owner, string? cursor, DateTimeOffset windowStart)
            => Observable.Return(new MailFeedStep(
                [new MailMessageCopy { Provider = "outlook", MessageId = "m1", Subject = "Hello", ReceivedAt = DateTimeOffset.UtcNow }],
                ImmutableList<string>.Empty, "c1", Done: true));
        public IObservable<IMailAttachmentSource> Attachments(string owner) => Observable.Return<IMailAttachmentSource>(new NoFiles());
    }

    /// <summary>A feed whose step is held until <see cref="Release"/>: a sync started before then parks in it.</summary>
    private sealed class HeldFeed : IMailCopyFeed
    {
        private readonly System.Reactive.Subjects.BehaviorSubject<bool> held = new(true);
        public void Release() => held.OnNext(false);
        public string Provider => "outlook";
        public IObservable<string> AccountId(string owner) => Observable.Return("acct");
        public IObservable<ImmutableList<(string AccountId, string Label)>> Accounts(string owner) => Observable.Return(ImmutableList.Create(("acct", "Mail")));
        public IObservable<MailFeedStep> Step(string owner, string? cursor, DateTimeOffset windowStart)
            => held.Where(h => !h).Take(1).Select(_ => new MailFeedStep(
                [new MailMessageCopy { Provider = "outlook", MessageId = "m1", Subject = "Hello", ReceivedAt = DateTimeOffset.UtcNow }],
                ImmutableList<string>.Empty, "c1", Done: true));
        public IObservable<IMailAttachmentSource> Attachments(string owner) => Observable.Return<IMailAttachmentSource>(new NoFiles());
    }

    private sealed class NoFiles : IMailAttachmentSource
    {
        public string Provider => "outlook";
        public string? AccountId => "acct";
        public IObservable<ImmutableList<MailAttachmentInfo>> List(string messageId) => Observable.Return(ImmutableList<MailAttachmentInfo>.Empty);
        public IObservable<byte[]?> Fetch(string messageId, string attachmentId) => Observable.Return<byte[]?>(null);
    }

    private sealed class NoFeed : IMailCopyFeed
    {
        public string Provider => "outlook";
        public IObservable<string> AccountId(string owner) => Observable.Empty<string>();
        public IObservable<ImmutableList<(string AccountId, string Label)>> Accounts(string owner) => Observable.Empty<ImmutableList<(string, string)>>();
        public IObservable<MailFeedStep> Step(string owner, string? cursor, DateTimeOffset windowStart) => Observable.Empty<MailFeedStep>();
        public IObservable<IMailAttachmentSource> Attachments(string owner) => Observable.Empty<IMailAttachmentSource>();
    }
}
