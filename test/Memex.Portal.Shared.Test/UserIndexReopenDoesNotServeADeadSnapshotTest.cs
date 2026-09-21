using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A user directory that is being REBUILT answers "I cannot say", never a stale hit and never
/// a definitive miss.</b> Found by review on core #5048.
///
/// <para><b>Why this exists.</b> Core #5048 made the query fan-in's stall a TERMINAL, which turned
/// <c>UserIdentityCache</c>'s once-only directory subscription from an eternal wait into an eternal
/// FAILURE — so that subscription is now re-openable: the dead chain is dropped and the next
/// <c>Lookup</c> opens a replacement (no timer, no poller). That re-open has a window, and the
/// window has exactly two ways to lie, both of them about IDENTITY, which is the most expensive
/// thing in the portal to be wrong about:</para>
/// <list type="number">
///   <item><description>Keep the dead chain's rows and <see cref="UserIdentityLookup.Classify"/>
///     answers <c>Found</c> off them — it checks the HIT before it checks either flag, by design —
///     so a user deleted or renamed since the terminal is still served. Retaining the unavailable
///     state does not close this: the hit outranks that too.</description></item>
///   <item><description>Clear the failure BEFORE lowering the hydrated flag and a reader sees
///     "healthy and authoritative" over the dead index, so a MISS is a definitive <b>Unknown</b> —
///     "no such user", the false actionable verdict #974/#637 exist to prevent and the exact input
///     that drives onboarding and provisioning.</description></item>
/// </list>
///
/// <para>The cure is <c>Apply</c>'s own Initial/Reset order, reused verbatim: down first, clear,
/// then un-fail. This test drives the three states through a REAL mesh — a real
/// <c>MeshService</c>, the real fan-in, the real cache — with a provider that answers the directory
/// query, then faults on the test's signal, then withholds its replacement snapshot until released.
/// Nothing is mocked; the provider is the extension point every storage backend plugs into, and
/// "faults after its first snapshot" is the shape <c>Classify</c>'s own doc calls out.</para>
/// </summary>
public class UserIndexReopenDoesNotServeADeadSnapshotTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string UserId = "reopen-probe-user";
    private const string Email = "reopen-probe@example.com";

    private readonly DirectoryProvider provider = new(UserId, Email);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .ConfigureServices(services => services.AddSingleton<IMeshQueryProvider>(provider));

    [Fact(Timeout = 240_000)]
    public async Task AReopenedIndexAnswersUnavailable_NeverAStaleHitAndNeverAFalseUnknown()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = new UserIdentityCache(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger<UserIdentityCache>());

        // ── 1. THE PRECONDITION. Without a Found here the rest measures nothing: an index that never
        //       filled would answer Unavailable for a reason that has nothing to do with the re-open.
        var hydrated = await Poll(cache, l => l.Node is not null, ct);
        hydrated.Node!.Id.Should().Be(UserId);

        // ── 2. Fault the live chain AFTER its first snapshot — the case Classify documents as "both
        //       set", where the index's contents are now arbitrarily stale.
        provider.FaultTheOpenChain();

        // ── 3. THE PROPERTY. Every reading from here until the replacement snapshot lands must be
        //       UNAVAILABLE. Not Found (that is the dead chain's row) and not Unknown (that is
        //       "no such user" about a user who exists). The replacement chain is HELD, so this is a
        //       stable state rather than a race: the poll cannot pass by getting lucky on timing.
        var readings = new List<UserIdentityLookup>();
        var degraded = await Poll(cache, l => l.IsUnavailable, ct, readings);
        degraded.IsUnavailable.Should().BeTrue();
        readings.Should().NotContain(l => l.Node is null && !l.IsUnavailable,
            "a definitive Unknown over a directory that is mid-rebuild says 'no such user' about a "
            + "user who exists — the false actionable verdict, and the input that drives onboarding");

        // Two more readings, taken while the replacement is still held, so the assertion is about a
        // STATE and not about the one instant the poll happened to sample.
        cache.Lookup(Email).IsUnavailable.Should().BeTrue();
        cache.Lookup(Email).IsUnavailable.Should().BeTrue();
        cache.Lookup(Email).Node.Should().BeNull(
            "the dead chain's row must be gone, not merely flagged — Classify answers Found on a hit "
            + "before it consults either flag");

        // ── 4. THE CONTROL on the repair itself: released, the replacement chain fills the index and
        //       the very same lookup answers again. Without this the fix could "pass" by permanently
        //       blacking the directory out, which is the opposite failure.
        provider.ReleaseTheReplacement();
        var repaired = await Poll(cache, l => l.Node is not null, ct);
        repaired.Node!.Id.Should().Be(UserId);
        provider.Subscriptions.Should().BeGreaterThanOrEqualTo(2,
            "the next Lookup after a terminal must OPEN A FRESH chain — nothing re-subscribes on its "
            + $"own, and the provider saw {provider.Subscriptions} subscription(s)");
    }

    /// <summary>
    /// Re-asks <see cref="UserIdentityCache.Lookup"/> until it satisfies <paramref name="until"/>.
    /// The sanctioned shape for a request/response source (there is no stream to filter — a lookup is
    /// a synchronous question), and every reading is recorded when <paramref name="readings"/> is
    /// given so an assertion can be made about the whole window rather than one sample.
    /// </summary>
    private static async Task<UserIdentityLookup> Poll(
        UserIdentityCache cache, Func<UserIdentityLookup, bool> until,
        CancellationToken ct, List<UserIdentityLookup>? readings = null)
        => await Observable.Interval(TestTimeouts.Quick / 100)
            .StartWith(0L)
            .Select(_ =>
            {
                var reading = cache.Lookup(Email);
                readings?.Add(reading);
                return reading;
            })
            .Where(until)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the cache has to reach the state under test", ct);

    /// <summary>
    /// Answers the <c>nodeType:User</c> directory query, then faults its OPEN chain on demand, then
    /// withholds the replacement chain's snapshot until released — so the re-open window is a state
    /// the test holds open rather than an instant it has to catch.
    ///
    /// <para>Queries other than the directory read are answered with a prompt EMPTY Initial (never
    /// <c>Observable.Empty</c>, which would make this the "completed without an Initial" case and
    /// change what is under test), so the rest of the mesh is untouched.</para>
    /// </summary>
    private sealed class DirectoryProvider(string userId, string email) : IMeshQueryProvider
    {
        public string Name => nameof(DirectoryProvider);

        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

        /// <summary>Test probe: how many times the directory read has been subscribed. A re-open is
        /// the second one.</summary>
        public int Subscriptions;

        // Completed to fault the chain that is currently open (arm 1), and to release the snapshot the
        // chain opened after it is holding back (arm 2). AsyncSubjects the TEST completes — the
        // sanctioned producer→test signal; nothing here waits on a gate.
        private readonly AsyncSubject<Unit> _fault = new();
        private readonly AsyncSubject<Unit> _release = new();

        public void FaultTheOpenChain()
        {
            _fault.OnNext(Unit.Default);
            _fault.OnCompleted();
        }

        public void ReleaseTheReplacement()
        {
            _release.OnNext(Unit.Default);
            _release.OnCompleted();
        }

        public IObservable<QueryResultChange<T>> Query<T>(
            MeshQueryRequest request, JsonSerializerOptions options)
        {
            if (!IsDirectoryRead(request))
                return Observable.Return(Frame<T>([]));

            var n = System.Threading.Interlocked.Increment(ref Subscriptions);
            var node = new MeshNode(userId)
            {
                Name = userId, NodeType = "User", State = MeshNodeState.Active,
                // 🚨 The REAL User content type. A loose dictionary is not convertible by
                // TryGetEmail's ContentAs<User>, so the node indexes under no email at all and the
                // precondition below fails with "not convertible" in the log — which is exactly what
                // it is there for, but it measures nothing about the re-open.
                Content = new MeshWeaver.Mesh.Security.User { Email = email, FullName = userId },
            };
            var snapshot = Frame<T>([(T)(object)node]);

            return n == 1
                // The FIRST chain answers, then faults on the test's signal — a fault AFTER the
                // first snapshot, which is the state Classify calls "both set".
                ? Observable.Return(snapshot)
                    .Concat(_fault.SelectMany(_ =>
                        Observable.Throw<QueryResultChange<T>>(
                            new InvalidOperationException("directory read faulted (test))"))))
                // Every LATER chain is the re-open: it withholds its snapshot until released, so the
                // window the test asserts about is stable.
                : _release.Select(_ => snapshot).Concat(Observable.Never<QueryResultChange<T>>());
        }

        private static QueryResultChange<T> Frame<T>(T[] items) => new()
        {
            ChangeType = QueryChangeType.Initial,
            Items = items,
            Timestamp = DateTimeOffset.UtcNow,
        };

        private static bool IsDirectoryRead(MeshQueryRequest request)
            => request.EffectiveQueries.Any(q =>
                q.Contains("nodeType:User", StringComparison.OrdinalIgnoreCase));

        public IObservable<IReadOnlyCollection<QueryResult>> Query(
            MeshQueryRequest request, JsonSerializerOptions options)
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
