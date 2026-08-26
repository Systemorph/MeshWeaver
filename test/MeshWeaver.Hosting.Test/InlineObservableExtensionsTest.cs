using System;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Holds <see cref="InlineObservableExtensions.ToInlineObservable{T}"/> to the two properties its
/// callers rely on, both of which the type's own remarks assert in prose.
/// </summary>
public class InlineObservableExtensionsTest
{
    /// <summary>
    /// The whole point: emission happens during <c>Subscribe</c>, on the subscribing thread, with no
    /// dependence on whether an Rx trampoline is already running there. The parameterless
    /// <c>ToObservable()</c> fails this inside a trampoline — see
    /// <see cref="LiveQueryForeignTrampolineTest"/> for what that costs.
    /// </summary>
    [Fact]
    public void Emits_during_Subscribe_even_inside_a_trampoline()
        // A fresh thread, or opening the trampoline can silently no-op — see FreshThread.
        => FreshThread.Run(
            () =>
            {
                var outside = 0;
                Enumerable.Range(0, 3).ToInlineObservable().Subscribe(_ => outside++);
                Assert.Equal(3, outside);

                var inside = 0;
                var wasInsideTrampoline = false;
                CurrentThreadScheduler.Instance.Schedule(() =>
                {
                    wasInsideTrampoline = !CurrentThreadScheduler.IsScheduleRequired;
                    Enumerable.Range(0, 3).ToInlineObservable().Subscribe(_ => inside++);
                    // Read the counter HERE, before returning to the trampoline: a deferred
                    // iteration would still be sitting in its queue at this point.
                    Assert.Equal(3, inside);
                });
                Assert.True(wasInsideTrampoline, "the probe never ran inside a trampoline");
            },
            "the probe thread never finished");

    /// <summary>
    /// 🚨 <see cref="ImmediateScheduler"/>'s recursive form is trampolined through a per-call
    /// <c>AsyncLock</c>, so a long sequence iterates rather than recursing. Without that, swapping
    /// <c>CurrentThreadScheduler</c> for <c>ImmediateScheduler</c> would trade a silent strand for a
    /// StackOverflowException — uncatchable, and fatal to the process — on any large directory
    /// listing. Measured: 500k elements in ~0.1 s.
    /// </summary>
    [Fact]
    public void A_long_sequence_iterates_without_growing_the_stack()
    {
        var count = 0;
        Enumerable.Range(0, 500_000).ToInlineObservable().Subscribe(_ => count++);
        Assert.Equal(500_000, count);
    }
}
