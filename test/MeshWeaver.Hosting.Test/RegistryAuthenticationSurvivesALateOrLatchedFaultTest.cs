using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <b>The plan ladder's live listing does not freeze on a fault that arrives AFTER it hydrated
/// (MeshWeaver#7354, review of #7729).</b>
///
/// <para>The concern: the listing is a <c>Replay(1)</c>, so a reader's <c>Take(1)</c> might take the
/// buffered snapshot and unsubscribe before the latched fault, the cache's fault eviction would never
/// run, and every later read would decide with the last pre-fault snapshot. It does not happen: a
/// replay delivers the snapshot AND the latched fault inside the one <c>Subscribe</c> call, before the
/// reader's unsubscription can take effect, so the per-subscriber eviction hook sees the fault and
/// drops the chain, and the NEXT read opens a fresh one. This test pins that, so a change to the
/// replay or the eviction hook that breaks it goes red here.</para>
///
/// <para>The mesh is real. The one injected piece is an <see cref="IMeshQueryProvider"/> — the
/// extension point every backend plugs into — that answers the tier listing and then fails on a switch
/// the test throws. A re-open is answered with one MORE tier, so "a later read sees the new tier" can
/// only mean the faulted listing was dropped and re-opened.</para>
/// </summary>
public class PlanLadderReopensAfterALatchedFaultTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly TierFaultProvider provider = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddPluginCatalog()
        .ConfigureServices(services => services.AddSingleton<IMeshQueryProvider>(provider));

    [Fact]
    public async Task AFaultAfterHydration_IsObserved_AndALaterReadSeesTheReopenedListing()
    {
        var ct = TestContext.Current.CancellationToken;
        var ladder = Mesh.ServiceProvider.GetRequiredService<PlanTierLadder>();

        var first = await ladder.Read().Should().Within(TestTimeouts.Convergence)
            .Emit("the ladder hydrates", cancellationToken: ct);
        first.Ids.Should().Contain("personal");
        first.Ids.Should().NotContain("pro", "the first listing carries one tier");
        var opened = provider.TierSubscriptions;

        provider.Fail(new InvalidOperationException("the tier listing's provider failed after answering"));

        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => ladder.Read())
            .Where(ranks => ranks.Ids.Contains("pro"))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a fault after hydration is observed and the listing re-opened — a read that took the "
                  + "replayed snapshot past the fault would decide with frozen ranks forever",
                cancellationToken: ct);
        provider.TierSubscriptions.Should().BeGreaterThan(opened, "the re-open asked the provider again");
    }

    private sealed class TierFaultProvider : IMeshQueryProvider
    {
        private readonly AsyncSubject<Exception> fault = new();
        private int tierSubscriptions;

        public int TierSubscriptions => Volatile.Read(ref tierSubscriptions);

        public void Fail(Exception error)
        {
            fault.OnNext(error);
            fault.OnCompleted();
        }

        public string Name => nameof(TierFaultProvider);

        public bool Matches(IReadOnlyList<string> queryNamespaces) => false;

        private static MeshNode Tier(string id, int rank) => MeshNode.FromPath($"{PlanTierLadder.Namespace}/{id}") with
        {
            NodeType = "Markdown",
            Content = JsonSerializer.SerializeToElement(new { rank, allAccess = false }),
        };

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options) =>
            Observable.Defer(() =>
            {
                QueryResultChange<T> Initial(params MeshNode[] nodes) => new()
                {
                    ChangeType = QueryChangeType.Initial,
                    Items = nodes.Cast<T>().ToArray(),
                    Timestamp = DateTimeOffset.UtcNow,
                };
                var tiers = request.EffectiveQueries.Any(q =>
                    q.Contains(PlanTierLadder.Namespace, StringComparison.OrdinalIgnoreCase));
                if (!tiers || typeof(T) != typeof(MeshNode))
                    return Observable.Return(Initial());
                Interlocked.Increment(ref tierSubscriptions);
                // Already failed ⇒ a re-open: healthy, one more tier, and it stays open.
                if (fault.IsCompleted)
                    return Observable.Return(Initial(Tier("personal", 1), Tier("pro", 2)))
                        .Concat(Observable.Never<QueryResultChange<T>>());
                return Observable.Return(Initial(Tier("personal", 1)))
                    .Concat(fault.SelectMany(ex => Observable.Throw<QueryResultChange<T>>(ex)));
            });

        public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request, JsonSerializerOptions options)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Observable.Return<T?>(default);
    }
}

/// <summary>
/// 🚨 <b>A held mirror whose fault is delivered LATE must not unhold its replacement
/// (MeshWeaver#7354, review of #7729).</b> A request can reach the hold after the cache has already
/// evicted a faulted mirror and handed out a healthy one, but before the old mirror's terminal has
/// been delivered to its holder. Skipping the hold because a slot existed, and then letting the late
/// terminal remove that slot, left the healthy replacement unheld for the idle sweep.
///
/// <para>The mirrors here are raw observables that keep their observer after unsubscription, so the
/// test can deliver the old terminal at exactly the late moment the pooled callback would.</para>
/// </summary>
public class RegistryHoldSurvivesALateTerminalTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Path = "acme/MeshWeaverInstance/prod";

    /// <summary>A mirror that records its observers and never detaches them, like a terminal
    /// already queued on a pool when the subscription is disposed.</summary>
    private sealed class LateMirror : IObservable<MeshNode>
    {
        private ImmutableList<IObserver<MeshNode>> observers = ImmutableList<IObserver<MeshNode>>.Empty;
        private int live;

        public int Live => Volatile.Read(ref live);

        public IDisposable Subscribe(IObserver<MeshNode> observer)
        {
            ImmutableInterlocked.Update(ref observers, list => list.Add(observer));
            Interlocked.Increment(ref live);
            return Disposable.Create(() => Interlocked.Decrement(ref live));
        }

        public void FaultLate(Exception error)
        {
            foreach (var observer in observers)
                observer.OnError(error);
        }
    }

    [Fact]
    public void ALateTerminalOfTheOldMirror_LeavesTheReplacementHeld()
    {
        var old = new LateMirror();
        var replacement = new LateMirror();
        var served = new Queue<LateMirror>(new[] { old, replacement });
        using var authenticator = new InstanceRegistryAuthenticator(GetHost(), NullLogger<InstanceRegistryAuthenticator>.Instance)
        {
            HoldOverride = _ => served.Dequeue(),
        };

        authenticator.Hold(Path);
        old.Live.Should().Be(1, "the first request holds the mirror it read");

        // The cache evicted the faulted mirror and the retry read a healthy one…
        authenticator.Hold(Path);
        // …and only now does the old mirror's terminal reach its holder.
        old.FaultLate(new IOException("owner unreachable"));

        authenticator.HeldPaths.Should().Contain(Path,
            "the late terminal of the OLD mirror releases only its own slot — the replacement stays held");
        replacement.Live.Should().Be(1, "the replacement mirror keeps its standing subscriber, so the idle sweep cannot release it");
    }

    /// <summary>
    /// Overlapping renewals (review of #7729): renewal A subscribes to the OLD mirror and pauses
    /// before publishing; renewal B, started later, subscribes to the replacement and publishes. When A
    /// resumes it must NOT overwrite B — replacing unconditionally disposed B's healthy hold, and A's
    /// own terminal then left the path unheld. The pause is the hook between subscription and
    /// publication, which runs B to completion inside A's window, deterministically.
    /// </summary>
    [Fact]
    public void AnOlderRenewalPausedBeforePublishing_DoesNotOverwriteALaterOne()
    {
        var old = new LateMirror();
        var replacement = new LateMirror();
        var served = new Queue<LateMirror>(new[] { old, replacement });
        var paused = 0;
        InstanceRegistryAuthenticator? authenticator = null;
        authenticator = new InstanceRegistryAuthenticator(GetHost(), NullLogger<InstanceRegistryAuthenticator>.Instance)
        {
            HoldOverride = _ => served.Dequeue(),
            // A is paused here; B (the next renewal) runs start to finish inside A's window.
            BeforePublish = path =>
            {
                if (Interlocked.Exchange(ref paused, 1) == 0)
                    authenticator!.Hold(path);
            },
        };
        using var _ = authenticator;

        authenticator.Hold(Path);
        // A's mirror was the old one: its fault is delivered now.
        old.FaultLate(new IOException("owner unreachable"));

        authenticator.HeldPaths.Should().Contain(Path, "B, the later renewal, still holds the path");
        replacement.Live.Should().Be(1, "B's subscription on the replacement mirror was not disposed by A's late publication");
        old.Live.Should().Be(0, "A, superseded, disposed its own subscription instead of publishing");
    }
}
