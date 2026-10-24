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
