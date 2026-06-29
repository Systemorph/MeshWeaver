using System;
using System.Reactive.Linq;
using MeshWeaver.Layout.DataBinding;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Layout.Test;

public class DebounceTest
{
    [Fact]
    public void BasicDebounce()
    {
        // Virtual-time test via Rx TestScheduler — no Task.Delay, no
        // wall-clock racing. Verifies the canonical Debounce contract:
        //   * a burst of rapid emissions collapses to its last value
        //   * emissions separated by ≥ the debounce window survive
        //
        // Two bursts of 3 emissions each, separated by a 100 t pause (window = 50 t).
        // Expected output: exactly 2 emissions (3 and 6) at the post-burst settle times.
        var scheduler = new TestScheduler();

        var source = scheduler.CreateHotObservable(
            ReactiveTest.OnNext(10, 1),
            ReactiveTest.OnNext(15, 2),
            ReactiveTest.OnNext(20, 3),    // burst-1 tail
            ReactiveTest.OnNext(150, 4),
            ReactiveTest.OnNext(155, 5),
            ReactiveTest.OnNext(160, 6),   // burst-2 tail
            ReactiveTest.OnCompleted<int>(200));

        var observer = scheduler.CreateObserver<int>();
        source
            .Debounce(TimeSpan.FromTicks(50), scheduler)
            .Subscribe(observer);

        scheduler.Start();

        observer.Messages.Should().HaveCount(3);  // 2 OnNext + 1 OnCompleted
        observer.Messages[0].Value.Value.Should().Be(3);
        observer.Messages[1].Value.Value.Should().Be(6);
        observer.Messages[2].Value.Kind.Should().Be(System.Reactive.NotificationKind.OnCompleted);
    }
}
