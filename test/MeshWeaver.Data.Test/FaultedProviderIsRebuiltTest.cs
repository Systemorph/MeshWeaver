using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 <b>Issue #3155 — one lost reply froze a collection for the life of the hub.</b>
///
/// <para>A <c>SubscribeRequest</c> from the per-pod cache hub to the <c>Store</c> hub timed out
/// after 60 s. The provider faulted, and <c>VirtualDataSource</c>'s error arm logged
/// <i>"that collection is frozen at its last emission and will receive no further updates"</i> —
/// accurately. The store/package listing on that pod was then stale permanently, from a single
/// timed-out request, until the hub was recycled.</para>
///
/// <para>The error arm itself is not the bug and must stay: Rx's default one-argument
/// <c>onError</c> is <c>Stubs.Throw</c>, which rethrows on whatever thread carried the fault, and
/// in #2468 that was a TimerQueue thread — an unhandled exception, a core-dumped host, and a gate
/// that "failed before it produced a verdict". What was missing is any recovery after it.</para>
///
/// <para>🚨 <b>And a plain retry would have been inert.</b> The cached chain is
/// <c>Replay(1).RefCount()</c>, and a <c>ReplaySubject</c> that has seen <c>OnError</c> LATCHES it:
/// every later subscriber gets the same fault replayed immediately, for ever. Re-subscribing
/// without dropping the cache is recovery inside the failed component — it would spin at whatever
/// rate the retry allows and never recover. That is the property this fixture pins first, because
/// it is the reason the fix has two halves rather than one.</para>
/// </summary>
public class FaultedProviderIsRebuiltTest(ITestOutputHelper output) : HubTestBase(output)
{
    private sealed record Item(string Id);

    /// <summary>
    /// The bare host registers no <c>IWorkspace</c>; a virtual type source needs one because its
    /// <c>TypeDefinition</c> is resolved off <c>Workspace.Hub.TypeRegistry</c> at construction.
    /// One trivial data source is enough — nothing here reads from it.
    /// </summary>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) =>
        base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(source =>
                source.WithType<Item>(type => type
                    .WithKey(instance => instance.Id)
                    .WithInitialData(_ => Observable.Return<IEnumerable<Item>>([])))));

    /// <summary>
    /// 🚨 THE TRAP. A faulted cached chain replays its fault to every later subscriber — so a retry
    /// that merely re-subscribes can never recover, however long it waits.
    /// </summary>
    [Fact]
    public void AFaultedCachedChain_ReplaysTheFaultToEveryLaterSubscriber()
    {
        var workspace = GetHost().GetWorkspace();
        var builds = 0;
        var source = new Subject<IEnumerable<Item>>();

        var typeSource = new VirtualTypeSource<Item>(
            workspace, "probe", _ => { builds++; return source; });

        using var first = typeSource.GetStreamUpdates().Subscribe(_ => { }, _ => { });
        builds.Should().Be(1, "the first subscriber starts the provider");

        source.OnError(new TimeoutException("no response received within 00:01:00"));

        Exception? replayed = null;
        using var second = typeSource.GetStreamUpdates().Subscribe(_ => { }, ex => replayed = ex);

        replayed.Should().BeOfType<TimeoutException>(
            "Replay(1) latches OnError — a later subscriber gets the SAME fault back immediately, "
            + "which is why re-subscribing alone can never rebuild a faulted provider (#3155)");
        builds.Should().Be(1,
            "and the provider was NOT re-invoked — the corpse was served from cache");
    }

    /// <summary>
    /// THE PIN. Evicting the cached chain makes the next subscribe rebuild it from the configured
    /// provider — which is what turns the retry from inert into a recovery.
    /// </summary>
    [Fact]
    public void EvictingTheCachedChain_RebuildsFromTheProvider()
    {
        var workspace = GetHost().GetWorkspace();
        var builds = 0;
        var sources = new List<Subject<IEnumerable<Item>>>();

        var typeSource = new VirtualTypeSource<Item>(
            workspace,
            "probe",
            _ =>
            {
                builds++;
                var s = new Subject<IEnumerable<Item>>();
                sources.Add(s);
                return s;
            });

        using var first = typeSource.GetStreamUpdates().Subscribe(_ => { }, _ => { });
        sources[0].OnError(new TimeoutException("no response received within 00:01:00"));

        typeSource.EvictCachedStream();

        IEnumerable<object>? seen = null;
        Exception? faulted = null;
        using var second = typeSource.GetStreamUpdates().Subscribe(x => seen = x, ex => faulted = ex);

        builds.Should().Be(2, "the provider is asked again once the latched chain is dropped");
        faulted.Should().BeNull("the rebuilt chain carries none of the old chain's fault");

        sources[1].OnNext([new Item("recovered")]);
        seen.Should().NotBeNull("and it delivers again — the collection is no longer frozen");
    }

    /// <summary>
    /// 🚨 Unbounded in COUNT — a budget would only be a slower version of the same defect, since the
    /// collection's life is the hub's — and bounded in RATE, which is what keeps that safe.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(7, 60)]     // 64s would exceed the ceiling
    [InlineData(40, 60)]
    public void TheBackoffDoublesThenSaturates(int attempt, int expectedSeconds)
        => VirtualDataSource.ProviderRetryDelay(attempt).Should()
            .Be(TimeSpan.FromSeconds(expectedSeconds),
                "a provider whose upstream is genuinely gone must cost one rebuild a minute, not a spin");

    /// <summary>
    /// 🚨 The loop is unbounded, so "the counter cannot get that high" is not an argument. A naive
    /// <c>1 &lt;&lt; (attempt - 1)</c> overflows into a NEGATIVE delay around attempt 63, and a
    /// negative timer delay fires immediately — turning the rate ceiling into a spin at exactly the
    /// moment the outage has lasted longest.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(63)]
    [InlineData(int.MaxValue)]
    public void TheBackoffIsNeverNegative_HoweverManyAttemptsHavePassed(int attempt)
    {
        var delay = VirtualDataSource.ProviderRetryDelay(attempt);

        delay.Should().BeGreaterThan(TimeSpan.Zero, "a non-positive delay fires immediately — a spin");
        delay.Should().BeLessThanOrEqualTo(VirtualDataSource.ProviderRetryMaxDelay,
            "the ceiling is the whole safety argument for retrying without a budget");
    }
}
