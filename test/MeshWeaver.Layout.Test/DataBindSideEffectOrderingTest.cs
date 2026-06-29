using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the data-binding ordering invariant exercised by
/// <c>LayoutClientExtensions.DataBind</c> + <c>BlazorView.DataBind</c>:
/// <b>the value converter runs BEFORE the property setter</b>. Concretely,
/// the binding pipeline is shaped
/// <c>source.Select(convert).Where(non-null).DistinctUntilChanged().Subscribe(setter)</c>,
/// so a side effect placed inside <c>convert</c> observes the View's state as
/// it was BEFORE the current emission was applied — typically empty on the
/// first emission.
///
/// <para>The regression these tests guard against: ThreadChatView was
/// calling <c>SyncMessageSubscriptions(messages)</c> inside its converter.
/// Per-message cache subscriptions need <c>threadPath</c>, which is set by
/// the property setter that runs AFTER the converter. On the first emission
/// <c>threadPath</c> was still empty, the helper bailed at its
/// <c>string.IsNullOrEmpty(threadPath)</c> guard, no subscriptions opened,
/// and every message rendered as a skeleton bar forever. No exception is
/// thrown — the symptom is silent.</para>
///
/// <para>The fix: any side effect that depends on state assigned by the
/// property setter belongs IN the setter, not in the converter. These tests
/// codify that invariant with a faithful reproduction of the pipeline.</para>
/// </summary>
public class DataBindSideEffectOrderingTest
{
    /// <summary>Stand-in for the value the layout area pushes through DataBind.</summary>
    private sealed record FakeViewModel(string? Path, IReadOnlyList<string> Items);

    /// <summary>
    /// Minimal stand-in for a BlazorView component. The property setter assigns
    /// <see cref="Path"/> from the incoming value AND records the value of
    /// <see cref="Path"/> at the time each side effect ran.
    /// </summary>
    private sealed class FakeView
    {
        public string? Path { get; private set; }
        public List<string> ConverterSawPath { get; } = new();
        public List<string> SetterSawPath { get; } = new();
        public Dictionary<string, string> OpenedSubscriptionsForItem { get; } = new();

        public void RecordInConverter() => ConverterSawPath.Add(Path ?? "(null)");

        public void ApplyValue(FakeViewModel? value, bool openSubscriptionsInSetter)
        {
            // Mirror the real setter: assign dependent state FIRST.
            if (value?.Path is not null)
                Path = value.Path;

            SetterSawPath.Add(Path ?? "(null)");

            if (openSubscriptionsInSetter && value is not null)
            {
                foreach (var item in value.Items)
                    OpenSubscription(item);
            }
        }

        public void OpenSubscription(string item)
        {
            // The bug: when Path is empty/null, the real code's early-return
            // guard bails and no subscription is opened. We mirror that by
            // refusing to record anything when Path is empty.
            if (string.IsNullOrEmpty(Path))
                return;
            OpenedSubscriptionsForItem[item] = $"{Path}/{item}";
        }
    }

    /// <summary>
    /// Reproduces the exact shape of <c>LayoutClientExtensions.DataBind</c>:
    /// <c>Select(convert).Where(non-null).DistinctUntilChanged().Subscribe(setter)</c>.
    /// </summary>
    private static IDisposable BuildBinding(
        IObservable<FakeViewModel?> source,
        Func<FakeViewModel?, FakeViewModel?> convert,
        Action<FakeViewModel?> setter) =>
        source
            .Select(convert)
            .Where(x => x is not null)
            .DistinctUntilChanged()
            .Subscribe(setter);

    [Fact]
    public void Converter_RunsBeforeSetter_OnEveryEmission()
    {
        // This pins the architectural fact that DataBind's pipeline runs the
        // converter as a Select operator, BEFORE the Subscribe handler that
        // calls the setter. If this ever changes (e.g. setter-first), the
        // ordering invariant the fix relies on flips and the test fails loudly.
        var view = new FakeView();
        var subject = new Subject<FakeViewModel?>();

        using var _ = BuildBinding(
            subject,
            convert: vm =>
            {
                view.RecordInConverter();
                return vm;
            },
            setter: vm => view.ApplyValue(vm, openSubscriptionsInSetter: false));

        subject.OnNext(new FakeViewModel("p1", ["a", "b"]));

        view.ConverterSawPath.Should().HaveCount(1, "the converter ran exactly once");
        view.SetterSawPath.Should().HaveCount(1, "the setter ran exactly once");
        view.ConverterSawPath[0].Should().Be("(null)",
            "the converter sees the View's Path BEFORE the setter assigns it");
        view.SetterSawPath[0].Should().Be("p1",
            "the setter has already assigned Path before its post-set side effect runs");
    }

    [Fact]
    public void SideEffect_InConverter_LosesSubscriptions_OnFirstEmission()
    {
        // The bug ThreadChatView had. Side effect placed in the converter sees
        // an empty Path; OpenSubscription's empty-Path guard bails; no
        // subscriptions are opened for any item; if no second emission comes,
        // they stay closed forever — the skeleton-bubble symptom.
        var view = new FakeView();
        var subject = new Subject<FakeViewModel?>();

        using var _ = BuildBinding(
            subject,
            convert: vm =>
            {
                // BUG SHAPE: side effect requiring Path lives in the converter.
                if (vm is not null)
                    foreach (var item in vm.Items)
                        view.OpenSubscription(item);
                return vm;
            },
            setter: vm => view.ApplyValue(vm, openSubscriptionsInSetter: false));

        subject.OnNext(new FakeViewModel("p1", ["a", "b", "c"]));

        view.OpenedSubscriptionsForItem.Should().BeEmpty(
            "Path was still null when the converter's OpenSubscription calls ran, " +
            "so its empty-Path guard refused to open any subscription — this is " +
            "the exact failure mode that produced 9 skeleton bubbles forever");
    }

    [Fact]
    public void SideEffect_InSetter_OpensSubscriptions_OnFirstEmission()
    {
        // The fix. With the side effect placed in the property setter — which
        // runs AFTER Path has been assigned from the incoming value — the
        // empty-Path guard never trips and every item gets a subscription
        // opened on the very first emission.
        var view = new FakeView();
        var subject = new Subject<FakeViewModel?>();

        using var _ = BuildBinding(
            subject,
            convert: vm => vm, // FIX SHAPE: converter is pure projection only.
            setter: vm => view.ApplyValue(vm, openSubscriptionsInSetter: true));

        subject.OnNext(new FakeViewModel("p1", ["a", "b", "c"]));

        view.OpenedSubscriptionsForItem.Should().HaveCount(3,
            "every item had a subscription opened on the first emission " +
            "because Path was already set by the time the side effect ran");
        view.OpenedSubscriptionsForItem["a"].Should().Be("p1/a");
        view.OpenedSubscriptionsForItem["b"].Should().Be("p1/b");
        view.OpenedSubscriptionsForItem["c"].Should().Be("p1/c");
    }

    [Fact]
    public void SideEffect_InConverter_NeverRecoversWithoutUpstreamPush()
    {
        // Why the bug was permanent in real chats: in production the upstream
        // (the layout-area stream) typically pushes ONCE per stable thread.
        // With the side effect in the converter, that single push runs the
        // converter with Path still null, the empty-Path guard refuses every
        // OpenSubscription call, and ContainsKey-based dedup (used by the
        // real SyncMessageSubscriptions) means no retry happens even if the
        // setter later assigns Path. Result: forever-skeletons.
        var view = new FakeView();
        var subject = new Subject<FakeViewModel?>();

        using var _ = BuildBinding(
            subject,
            convert: vm =>
            {
                if (vm is not null)
                    foreach (var item in vm.Items)
                        view.OpenSubscription(item);
                return vm;
            },
            setter: vm => view.ApplyValue(vm, openSubscriptionsInSetter: false));

        subject.OnNext(new FakeViewModel("p1", ["a", "b"])); // single push, like prod

        view.Path.Should().Be("p1", "the setter assigned Path");
        view.OpenedSubscriptionsForItem.Should().BeEmpty(
            "the converter ran BEFORE the setter, so Path was empty when the " +
            "converter's OpenSubscription calls ran; no further upstream push " +
            "ever arrives, so the empty-Path guard's miss is permanent");
    }
}
