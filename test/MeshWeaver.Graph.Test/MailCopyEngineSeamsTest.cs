using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// What a provider needs of the generic engine beyond the sync, on a real mesh with a scripted feed: answering
/// the inbox from the copy once it is backfilled (and only for the account that made it), following the
/// person's own Delete and Undo, and settling an event that lost to a newer local action on the mailbox's
/// present state instead of dropping it.
/// </summary>
public class MailCopyEngineSeamsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);
    private static readonly DateTimeOffset Today = DateTimeOffset.UtcNow;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder).AddMailCopyTypes();

    // ── the read side ───────────────────────────────────────────────────────

    [Fact(Timeout = 180_000)]
    public async Task OnceBackfilled_TheInboxIsAnsweredFromTheCopy_OnlyForTheAccountThatMadeIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await Rig(ct);
        rig.Feed.Messages["m1"] = Message("m1", "Quarterly figures", "The invoice for Fabrikam", 1);
        rig.Feed.Messages["m2"] = Message("m2", "Lunch", "Pizza on Friday", 2);
        rig.Feed.Messages["m3"] = Message("m3", "Old news", "From before the window", 200);
        await Enable(rig, ct);

        var source = await rig.Engine.ServesFromCopy(rig.Owner).Timeout(Bound).Await(ct);
        source.Should().NotBeNull("backfilled and of the connected account");
        var all = await rig.Engine.ReadInbox(source!, rig.Owner, 10, null).Timeout(Bound).Await(ct);
        var found = await rig.Engine.ReadInbox(source!, rig.Owner, 10, "invoice fabrikam").Timeout(Bound).Await(ct);
        var one = await rig.Engine.StoredCopy(rig.Owner, "m2").Timeout(Bound).Await(ct);

        all.Select(c => c.MessageId).Should().Equal(["m1", "m2"], "newest first; the message before the window is not copied");
        found.Select(c => c.MessageId).Should().Equal(["m1"], "every word, in the body too");
        one!.Body.Should().Contain("Pizza");

        rig.Feed.Account = "acct-2";
        (await rig.Engine.ServesFromCopy(rig.Owner).Timeout(Bound).Await(ct)).Should().BeNull("a copy of the previous account is not served under the new one");
        (await rig.Engine.StoredCopy(rig.Owner, "m2").Timeout(Bound).Await(ct)).Should().BeNull("nor opened");
    }

    [Fact(Timeout = 180_000)]
    public async Task ACopyThatIsOff_IsServedFromNowhere_AndNothingIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await Rig(ct);
        rig.Feed.Messages["m1"] = Message("m1", "Hello", "Body", 1);

        var source = await rig.Engine.ServesFromCopy(rig.Owner).Timeout(Bound).Await(ct);
        await Task.Delay(300, ct);

        source.Should().BeNull();
        (await rig.Store.Source(rig.Owner, "outlook").Timeout(Bound).Await(ct)).Should().BeNull("a read never switches a copy on");
        rig.Feed.StepCalls.Should().Be(0, "a copy that is off asks the provider nothing");
    }

    // ── Delete and Undo ─────────────────────────────────────────────────────

    [Fact(Timeout = 180_000)]
    public async Task ADelete_RemovesTheCopy_AndAnUndo_BringsItBack_InsideTheWindowOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await Rig(ct);
        rig.Feed.Messages["m1"] = Message("m1", "Hello", "Body of one", 1);
        rig.Feed.Messages["old"] = Message("old", "Ancient", "Outside", 5);
        await Enable(rig, ct);

        (await rig.Engine.FollowMove(rig.Owner, "m1", removed: true).Timeout(Bound).Await(ct)).Should().BeTrue();
        (await rig.Engine.StoredCopy(rig.Owner, "m1").Timeout(Bound).Await(ct)).Should().BeNull("a tombstone is never opened");

        (await rig.Engine.FollowMove(rig.Owner, "m1", removed: false).Timeout(Bound).Await(ct)).Should().BeTrue();
        (await rig.Engine.StoredCopy(rig.Owner, "m1").Timeout(Bound).Await(ct))!.Body.Should().Be("Body of one");

        rig.Feed.Messages["far"] = Message("far", "Far back", "Outside the window", 400);
        (await rig.Engine.FollowMove(rig.Owner, "far", removed: false).Timeout(Bound).Await(ct)).Should().BeFalse("outside the window: the sync would not have copied it either");
    }

    [Fact(Timeout = 180_000)]
    public async Task ALateDelete_NeverUndoesWhatHappenedAfterIt_AndAnUndoAfterSwitchOff_LeavesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await Rig(ct);
        rig.Feed.Messages["m1"] = Message("m1", "Hello", "Body", 1);
        await Enable(rig, ct);
        var askedEarly = DateTimeOffset.UtcNow.AddMinutes(-5);

        await rig.Engine.FollowMove(rig.Owner, "m1", removed: true, askedEarly).Timeout(Bound).Await(ct);

        (await rig.Engine.StoredCopy(rig.Owner, "m1").Timeout(Bound).Await(ct)).Should().NotBeNull(
            "the copy was written AFTER the Delete was asked for; a late removal must never undo it");

        await rig.Engine.Disable(rig.Owner).Timeout(Bound).Await(ct);
        (await rig.Engine.FollowMove(rig.Owner, "m1", removed: false).Timeout(Bound).Await(ct)).Should().BeFalse("the copy is off");
        (await rig.Store.Source(rig.Owner, "outlook").Timeout(Bound).Await(ct)).Should().BeNull();
    }

    // ── an event that lost to a newer local action ──────────────────────────

    [Fact(Timeout = 180_000)]
    public async Task AnEventThatLostToANewerDelete_IsSettledOnTheMailboxsPresentState()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await Rig(ct);
        rig.Feed.Messages["m1"] = Message("m1", "Hello", "Body", 1);
        await Enable(rig, ct);
        // A Delete asked for (a minute from now): newer than anything the next sync writes.
        await rig.Engine.FollowMove(rig.Owner, "m1", removed: true, DateTimeOffset.UtcNow.AddMinutes(1)).Timeout(Bound).Await(ct);
        (await rig.Engine.StoredCopy(rig.Owner, "m1").Timeout(Bound).Await(ct)).Should().BeNull();
        // The provider still has the message, and its next step names it as changed — the write is refused.
        rig.Feed.Pending = [Message("m1", "Hello", "Body", 1)];

        await SyncAsync(rig, ct);

        rig.Feed.RereadCalls.Should().BeGreaterThan(0, "the refused event was settled by asking the provider");
        (await rig.Engine.StoredCopy(rig.Owner, "m1").Timeout(Bound).Await(ct)).Should().NotBeNull("the mailbox's present state wins over the guess");
    }

    [Fact(Timeout = 180_000)]
    public async Task AFeedThatCannotReread_LeavesTheRefusedEventDropped()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await Rig(ct, canReread: false);
        rig.Feed.Messages["m1"] = Message("m1", "Hello", "Body", 1);
        await Enable(rig, ct);
        await rig.Engine.FollowMove(rig.Owner, "m1", removed: true, DateTimeOffset.UtcNow.AddMinutes(1)).Timeout(Bound).Await(ct);
        rig.Feed.Pending = [Message("m1", "Hello", "Body", 1)];

        await SyncAsync(rig, ct);

        rig.Feed.RereadCalls.Should().Be(0);
        (await rig.Engine.StoredCopy(rig.Owner, "m1").Timeout(Bound).Await(ct)).Should().BeNull("nothing could settle it, so the newer Delete stands");
    }

    // ── rig ─────────────────────────────────────────────────────────────────

    private sealed record Rigging(string Owner, ScriptedFeed Feed, MeshMailCopyStore Store, MailCopyEngine Engine);

    private async Task<Rigging> Rig(CancellationToken ct, bool canReread = true)
    {
        var owner = $"user{Guid.NewGuid():N}"[..12];
        await SeedTopLevel(MeshNode.FromPath(owner) with
        {
            Name = "Jane Roe",
            NodeType = UserNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new User { Email = "jane@acme.example", FullName = "Jane Roe" },
        }).WaitAsync(ct);
        var feed = new ScriptedFeed { CanRereadFlag = canReread };
        var store = new MeshMailCopyStore(() => Mesh);
        var access = Mesh.ServiceProvider.GetService<AccessService>();
        return new Rigging(owner, feed, store, new MailCopyEngine(feed, store, access: access));
    }

    private async Task Enable(Rigging rig, CancellationToken ct)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        IObservable<MailCopySource> enable;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = rig.Owner, Name = rig.Owner }))
            enable = rig.Engine.Enable(rig.Owner, 90);
        await enable.Timeout(Bound).Await(ct);
        for (var i = 0; i < 200; i++)
        {
            if (await rig.Engine.Source(rig.Owner).Timeout(Bound).Await(ct) is { Backfilled: true, LeaseHolder: null })
                return;
            await Task.Delay(100, ct);
        }
        throw new InvalidOperationException("The backfill never finished.");
    }

    private static async Task SyncAsync(Rigging rig, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await rig.Engine.Sync(rig.Owner).Timeout(Bound).Await(ct) is not null)
                return;
            await Task.Delay(200, ct);
        }
        throw new InvalidOperationException("The sync never ran.");
    }

    private static MailMessageCopy Message(string id, string subject, string body, int daysAgo)
    {
        var received = Today.AddDays(-daysAgo);
        return new MailMessageCopy
        {
            MessageId = id,
            Subject = subject,
            Body = body,
            ReceivedAt = received,
            ReceivedTicks = received.ToUnixTimeMilliseconds(),
            OrderKey = MailCopyNodeTypes.OrderKeyOf(received.ToUnixTimeMilliseconds(), id),
        };
    }

    private sealed class NoAttachments : IMailAttachmentSource
    {
        public string Provider => "outlook";
        public string? AccountId => null;
        public IObservable<ImmutableList<MailAttachmentInfo>> List(string messageId) => Observable.Return(ImmutableList<MailAttachmentInfo>.Empty);
        public IObservable<byte[]?> Fetch(string messageId, string attachmentId) => Observable.Return<byte[]?>(null);
    }

    /// <summary>A feed over an in-memory mailbox: the first step lists everything, later steps return what was queued.</summary>
    private sealed class ScriptedFeed : IMailCopyFeed
    {
        public string Account { get; set; } = "acct-1";
        public Dictionary<string, MailMessageCopy> Messages { get; } = [];
        public ImmutableList<MailMessageCopy> Pending { get; set; } = [];
        public bool CanRereadFlag { get; init; } = true;
        public int RereadCalls;
        public int StepCalls;

        public string Provider => "outlook";
        public bool CanReread => CanRereadFlag;
        public IObservable<string> AccountId(string owner) => Observable.Return(Account);
        public IObservable<ImmutableList<(string AccountId, string Label)>> Accounts(string owner)
            => Observable.Return(ImmutableList.Create((Account, "Mail")));
        public IObservable<IMailAttachmentSource> Attachments(string owner) => Observable.Return<IMailAttachmentSource>(new NoAttachments());

        public IObservable<MailFeedStep> Step(string owner, string? cursor, DateTimeOffset windowStart)
            => Observable.Defer(() =>
            {
                Interlocked.Increment(ref StepCalls);
                if (cursor is null)
                    return Observable.Return(new MailFeedStep([.. Messages.Values], [], "c1", Done: true));
                var changed = Pending;
                Pending = [];
                return Observable.Return(new MailFeedStep(changed, [], "c1", Done: true));
            });

        public IObservable<MailMessageCopy?> Reread(string owner, string messageId)
            => Observable.Defer(() =>
            {
                Interlocked.Increment(ref RereadCalls);
                return Observable.Return(Messages.GetValueOrDefault(messageId));
            });
    }
}
