using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Memex.Portal.Shared.Email;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The portal's two e-mail senders hold ONE live query each for the life of the process — the
/// outbound-mail watch (<see cref="OutboundEmailSender"/>) and the invitation watch
/// (<see cref="InvitationEmailSender"/>). Each used to subscribe its query with an error arm that only
/// logged, so a single fault of the query (a stalled provider, a dropped connection) stopped every
/// outbound or invitation e-mail until the process restarted.
///
/// <para>Each test: the first open throws a <see cref="QueryProviderStalledException"/>, the watch
/// survives, the re-open runs on the test's manual clock, and a listing after the fault is handled.
/// <see cref="TheUnguardedShape_HandlesNothingAfterTheFault"/> is the negative control: the pre-fix
/// shape, driven by the same source, opens once and never again.</para>
/// </summary>
public class EmailWatchesReopenAfterAFaultTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private sealed class ManualReopenClock
    {
        private ImmutableQueue<Action> due = ImmutableQueue<Action>.Empty;

        public int Scheduled { get; private set; }

        public IDisposable Schedule(Action reopen)
        {
            due = due.Enqueue(reopen);
            Scheduled++;
            return Disposable.Empty;
        }

        public bool FireNext()
        {
            if (due.IsEmpty)
                return false;
            due = due.Dequeue(out var reopen);
            reopen();
            return true;
        }
    }

    private sealed class FlakySource
    {
        public Subject<IReadOnlyList<MeshNode>> Live { get; } = new();

        public int Opens { get; private set; }

        public IObservable<IReadOnlyList<MeshNode>> Open() => ++Opens == 1
            ? Observable.Throw<IReadOnlyList<MeshNode>>(
                new QueryProviderStalledException("test-provider", TimeSpan.FromSeconds(1), "q", "system"))
            : Live;
    }

    private delegate IDisposable WatchInstaller(
        Func<IObservable<IReadOnlyList<MeshNode>>> open,
        Action<IReadOnlyList<MeshNode>> onItems,
        Func<Action, IDisposable> scheduleReopen);

    private void ReopensAndHandlesTheNextListing(WatchInstaller install)
    {
        var clock = new ManualReopenClock();
        var source = new FlakySource();
        var handled = ImmutableList<string>.Empty;

        using var watch = install(source.Open, items =>
        {
            foreach (var node in items)
                handled = handled.Add(node.Path);
        }, clock.Schedule);

        source.Opens.Should().Be(1);
        clock.Scheduled.Should().Be(1, "the fault schedules a re-open instead of ending the watch");

        clock.FireNext().Should().BeTrue();
        source.Opens.Should().Be(2, "the re-open asks for a FRESH query");
        source.Live.HasObservers.Should().BeTrue();

        source.Live.OnNext([new MeshNode("after-the-fault", "Admin/Invitation")]);
        handled.Should().Equal("Admin/Invitation/after-the-fault");
    }

    [Fact]
    public void TheOutboundMailWatch_ReopensAfterAFault_AndHandlesTheNextListing() =>
        ReopensAndHandlesTheNextListing((open, onItems, schedule) =>
            OutboundEmailSender.SubscribeOutboundWatch(Mesh, open, onItems, logger: null, schedule));

    [Fact]
    public void TheInvitationWatch_ReopensAfterAFault_AndHandlesTheNextListing() =>
        ReopensAndHandlesTheNextListing((open, onItems, schedule) =>
            InvitationEmailSender.SubscribeInvitationWatch(Mesh, open, onItems, logger: null, schedule));

    /// <summary>
    /// The negative control: the pre-fix shape of both senders — the query subscribed once, the error
    /// arm logging — driven by the same flaky source. It opens once, nothing re-opens it, and a
    /// listing after the fault reaches nobody.
    /// </summary>
    [Fact]
    public void TheUnguardedShape_HandlesNothingAfterTheFault()
    {
        var source = new FlakySource();
        var handled = 0;
        var logged = 0;

        using var watch = source.Open().Subscribe(_ => handled++, _ => logged++);

        logged.Should().Be(1);
        source.Opens.Should().Be(1);
        source.Live.HasObservers.Should().BeFalse("nothing re-opened the query");
        source.Live.OnNext([new MeshNode("after-the-fault", "Admin/Invitation")]);
        handled.Should().Be(0);
    }
}
