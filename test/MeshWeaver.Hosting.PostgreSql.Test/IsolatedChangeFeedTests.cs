using System.Reactive.Disposables;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins the storage adapter's change-feed fan-out contract.
///
/// <para>The defect these cover cost three CI runs to see. The feed used to be a plain
/// <see cref="Subject{T}"/>, whose <c>OnNext</c> delivers synchronously in subscription order — so
/// the FIRST observer that throws aborts delivery to every observer after it — and the publish
/// sites wrapped that in <c>catch { }</c>, so it was silent. A synced query being torn down threw
/// <see cref="ObjectDisposedException"/> into the fan-out, and the <c>$security-access</c> query
/// behind it never received the notification: its <c>Replay(1)</c> froze at the pre-write snapshot
/// and the permission fold never completed.</para>
/// </summary>
public class IsolatedChangeFeedTests
{
    private static DataChangeNotification Change(string path) =>
        DataChangeNotification.Updated(path, MeshNode.FromPath(path));

    /// <summary>An observer that throws on every notification — the disposed-buffer shape.</summary>
    private sealed class Thrower : IObserver<DataChangeNotification>
    {
        public int Calls { get; private set; }
        public void OnNext(DataChangeNotification value)
        {
            Calls++;
            throw new ObjectDisposedException(nameof(Subject<DataChangeNotification>));
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class Recorder : IObserver<DataChangeNotification>
    {
        public List<string> Paths { get; } = [];
        public void OnNext(DataChangeNotification value) => Paths.Add(value.Path);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    [Fact]
    public void A_throwing_observer_does_not_starve_the_ones_after_it()
    {
        var feed = new IsolatedChangeFeed(null, "test");
        var thrower = new Thrower();
        var downstream = new Recorder();

        // Subscription ORDER is the whole point: the thrower is first, so a Subject would have
        // aborted before ever reaching `downstream`.
        feed.Subscribe(thrower);
        feed.Subscribe(downstream);

        feed.OnNext(Change("acme/Gated"));

        thrower.Calls.Should().Be(1);
        downstream.Paths.Should().Equal("acme/Gated");
    }

    [Fact]
    public void A_throwing_observer_is_dropped_and_the_rest_keep_receiving()
    {
        var feed = new IsolatedChangeFeed(null, "test");
        var thrower = new Thrower();
        var downstream = new Recorder();
        feed.Subscribe(thrower);
        feed.Subscribe(downstream);

        feed.OnNext(Change("one"));
        feed.OnNext(Change("two"));

        // An observer that throws out of OnNext has broken the Rx contract — it is removed rather
        // than re-invoked on every subsequent write.
        thrower.Calls.Should().Be(1);
        downstream.Paths.Should().Equal("one", "two");
    }

    [Fact]
    public void The_publish_never_throws_at_the_caller()
    {
        var feed = new IsolatedChangeFeed(null, "test");
        feed.Subscribe(new Thrower());

        // A write must never be turned into a failure by a subscriber — but that guarantee now
        // comes from per-observer isolation, not from a catch-all that also hid the starvation.
        var publish = () => feed.OnNext(Change("acme/X"));
        publish.Should().NotThrow();
    }

    [Fact]
    public void Disposing_a_subscription_stops_delivery_to_it_only()
    {
        var feed = new IsolatedChangeFeed(null, "test");
        var stays = new Recorder();
        var goes = new Recorder();
        feed.Subscribe(stays);
        var sub = feed.Subscribe(goes);

        feed.OnNext(Change("before"));
        sub.Dispose();
        feed.OnNext(Change("after"));

        stays.Paths.Should().Equal("before", "after");
        goes.Paths.Should().Equal("before");
    }

    [Fact]
    public void Subscribing_during_a_publish_does_not_disturb_the_fan_out()
    {
        var feed = new IsolatedChangeFeed(null, "test");
        var late = new Recorder();
        var tail = new Recorder();
        // Mutates the observer list WHILE OnNext is walking it — the snapshot is what makes this
        // safe (a List<T> would throw "collection was modified" mid-fan-out).
        feed.Subscribe(new AnonymousObserver(_ => feed.Subscribe(late)));
        feed.Subscribe(tail);

        var publish = () => feed.OnNext(Change("during"));

        publish.Should().NotThrow();
        tail.Paths.Should().Equal("during");   // the observer after the mutator still got it
        late.Paths.Should().BeEmpty();          // the late subscriber only sees LATER notifications
    }

    private sealed class AnonymousObserver(Action<DataChangeNotification> onNext) : IObserver<DataChangeNotification>
    {
        public void OnNext(DataChangeNotification value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>
    /// GUARD THE GUARD. Pins the <see cref="Subject{T}"/> behaviour the feed exists to avoid, so
    /// the tests above cannot quietly become vacuous: if a future Rx made Subject isolate its
    /// observers, this fails and the whole IsolatedChangeFeed rationale needs revisiting.
    ///
    /// <para>This is also the proof that those tests are meaningful — swap IsolatedChangeFeed for
    /// a Subject and "does not starve the ones after it" fails exactly here.</para>
    /// </summary>
    [Fact]
    public void Subject_aborts_the_fan_out_at_the_first_thrower_which_is_why_we_do_not_use_one()
    {
        var subject = new Subject<DataChangeNotification>();
        var downstream = new Recorder();
        subject.Subscribe(new Thrower());
        subject.Subscribe(downstream);

        // The throw escapes the publish AND the later observer never ran — the two halves of the
        // defect. The old publish sites then wrapped this in `catch { }`, hiding both.
        var publish = () => subject.OnNext(Change("acme/Gated"));
        publish.Should().Throw<ObjectDisposedException>();
        downstream.Paths.Should().BeEmpty();
    }

    /// <summary>
    /// The disposal-order half of the fix, at the shape level: a CompositeDisposable disposes in
    /// INSERTION order, so a buffer registered before the subscription that feeds it is dead while
    /// that subscription is still live — which is exactly how the thrower above came to exist.
    /// </summary>
    [Fact]
    public void CompositeDisposable_disposes_in_insertion_order()
    {
        var order = new List<string>();
        var composite = new CompositeDisposable
        {
            Disposable.Create(() => order.Add("first")),
            Disposable.Create(() => order.Add("second")),
        };

        composite.Dispose();

        order.Should().Equal("first", "second");
    }
}
