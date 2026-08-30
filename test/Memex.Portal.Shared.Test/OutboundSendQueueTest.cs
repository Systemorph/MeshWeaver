using System.Collections.Concurrent;
using System.Reactive.Linq;
using Memex.Portal.Shared.Email;
using MeshWeaver.Mesh;
using Xunit;
using MeshWeaver.Fixture;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the fix for the outbound DOUBLE-DELIVERY observed live on 2026-08-21: every queued mail
/// arrived twice, because the watch legitimately emits the same <c>New</c> snapshot more than once
/// (initial snapshot + change echo — the reason tests must never assert "exactly N change events")
/// and the old New → Sending "claim" was a plain last-write-wins update whose "claim failed" guard
/// was unreachable. <see cref="OutboundSendQueue"/> serializes sends and re-reads the mail's
/// authoritative state before each one; these cases drive that state machine through delegate
/// seams over a tiny in-test store — deterministic, no hub, no timing sleeps.
/// </summary>
public class OutboundSendQueueTest
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(10);

    private sealed class Harness : IDisposable
    {
        public readonly ConcurrentDictionary<string, MeshWeaver.Mesh.Email> Store = new();
        public readonly ConcurrentDictionary<string, int> Sends = new();
        public Func<MeshWeaver.Mesh.Email, IObservable<bool>>? SendOverride;
        public Func<string, IObservable<MeshWeaver.Mesh.Email?>>? ReadOverride;
        public OutboundSendQueue Queue { get; }

        public Harness(TimeSpan? readBudget = null)
        {
            Queue = new OutboundSendQueue(
                // Defer: the read must observe the store AS OF the moment the queue processes the
                // item, not as of enqueue — that freshness is the very property under test.
                readCurrent: path => ReadOverride?.Invoke(path)
                    ?? Observable.Defer(() => Observable.Return(
                        Store.TryGetValue(path, out var email) ? email : null)),
                writeStatus: (node, current, status) => Observable.Defer(() =>
                {
                    Store[node.Path] = current with { Status = status };
                    return Observable.Return(node);
                }),
                send: email =>
                {
                    Sends.AddOrUpdate(email.To ?? "", 1, (_, n) => n + 1);
                    return SendOverride?.Invoke(email) ?? Observable.Return(true);
                },
                readBudget: readBudget);
        }

        public MeshNode QueuedMail(string id, string to = "one@example.test")
        {
            var email = new MeshWeaver.Mesh.Email
            {
                Direction = EmailDirection.Outbound,
                Status = EmailStatus.New,
                To = to,
                Subject = "s",
                Body = "b",
            };
            var node = new MeshNode(id, "T") { NodeType = "Email", Name = id, Content = email };
            Store[node.Path] = email;
            return node;
        }

        public Task<IList<string>> AwaitProcessed(int count) =>
            Queue.Processed.Take(count).ToList().Timeout(TestBudget).FirstAsync().Await();

        public void Dispose() => Queue.Dispose();
    }

    [Fact]
    public async Task DuplicateEmissions_SendExactlyOnce()
    {
        using var h = new Harness();
        var node = h.QueuedMail("m1");
        var email = (MeshWeaver.Mesh.Email)node.Content!;

        // The live failure mode: the SAME New snapshot delivered twice by the watch. The awaiter
        // is armed FIRST: the seams are synchronous, so processing completes inside Enqueue.
        var done = h.AwaitProcessed(2);
        h.Queue.Enqueue(node, email);
        h.Queue.Enqueue(node, email);
        await done;

        Assert.Equal(1, h.Sends["one@example.test"]);
        Assert.Equal(EmailStatus.Sent, h.Store[node.Path].Status);
    }

    [Fact]
    public async Task StaleEchoAfterCompletion_DoesNotResend()
    {
        using var h = new Harness();
        var node = h.QueuedMail("m1");
        var email = (MeshWeaver.Mesh.Email)node.Content!;

        var first = h.AwaitProcessed(1);
        h.Queue.Enqueue(node, email);
        await first;
        // A late echo of the long-finished New snapshot — the re-read sees Sent and skips.
        var echo = h.AwaitProcessed(1);
        h.Queue.Enqueue(node, email);
        await echo;

        Assert.Equal(1, h.Sends["one@example.test"]);
        Assert.Equal(EmailStatus.Sent, h.Store[node.Path].Status);
    }

    [Fact]
    public async Task AlreadyClaimedElsewhere_IsSkipped()
    {
        using var h = new Harness();
        var node = h.QueuedMail("m1");
        var email = (MeshWeaver.Mesh.Email)node.Content!;
        h.Store[node.Path] = email with { Status = EmailStatus.Sending };

        var done = h.AwaitProcessed(1);
        h.Queue.Enqueue(node, email);
        await done;

        Assert.Empty(h.Sends);
        Assert.Equal(EmailStatus.Sending, h.Store[node.Path].Status);
    }

    [Fact]
    public async Task FailedSend_MarksFailed()
    {
        using var h = new Harness();
        h.SendOverride = _ => Observable.Return(false);
        var node = h.QueuedMail("m1");

        var done = h.AwaitProcessed(1);
        h.Queue.Enqueue(node, (MeshWeaver.Mesh.Email)node.Content!);
        await done;

        Assert.Equal(1, h.Sends["one@example.test"]);
        Assert.Equal(EmailStatus.Failed, h.Store[node.Path].Status);
    }

    [Fact]
    public async Task SendError_MarksFailed_AndTheQueueContinues()
    {
        using var h = new Harness();
        h.SendOverride = email => email.To == "boom@example.test"
            ? Observable.Throw<bool>(new InvalidOperationException("graph down"))
            : Observable.Return(true);
        var poisoned = h.QueuedMail("m1", to: "boom@example.test");
        var healthy = h.QueuedMail("m2", to: "ok@example.test");

        var done = h.AwaitProcessed(2);
        h.Queue.Enqueue(poisoned, (MeshWeaver.Mesh.Email)poisoned.Content!);
        h.Queue.Enqueue(healthy, (MeshWeaver.Mesh.Email)healthy.Content!);
        await done;

        Assert.Equal(EmailStatus.Failed, h.Store[poisoned.Path].Status);
        Assert.Equal(EmailStatus.Sent, h.Store[healthy.Path].Status);
        Assert.Equal(1, h.Sends["ok@example.test"]);
    }

    [Fact]
    public async Task UnansweredStateRead_SkipsWithoutSending_AndTheQueueContinues()
    {
        using var h = new Harness(readBudget: TimeSpan.FromMilliseconds(100));
        var silent = h.QueuedMail("m1", to: "silent@example.test");
        var healthy = h.QueuedMail("m2", to: "ok@example.test");
        h.ReadOverride = path => path == silent.Path
            ? Observable.Never<MeshWeaver.Mesh.Email?>()
            : Observable.Defer(() => Observable.Return(
                h.Store.TryGetValue(path, out var email) ? email : (MeshWeaver.Mesh.Email?)null));

        var done = h.AwaitProcessed(2);
        h.Queue.Enqueue(silent, (MeshWeaver.Mesh.Email)silent.Content!);
        h.Queue.Enqueue(healthy, (MeshWeaver.Mesh.Email)healthy.Content!);
        await done;

        // Fail CLOSED: no send on an unanswerable read — the mail stays New for a later retry.
        Assert.False(h.Sends.ContainsKey("silent@example.test"));
        Assert.Equal(EmailStatus.New, h.Store[silent.Path].Status);
        Assert.Equal(EmailStatus.Sent, h.Store[healthy.Path].Status);
    }
}
