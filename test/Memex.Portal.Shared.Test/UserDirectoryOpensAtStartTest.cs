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
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚦 <b>The first signed-in request on a fresh process meets a HYDRATED user directory</b>
/// (MeshWeaver#6850).
///
/// <para>The directory (<see cref="UserIdentityCache"/>) is a DI singleton, so it was built by the
/// first request that resolved it, and that request then asked the index it had just created. The
/// answer was "the mesh user index has not received its first snapshot yet" on every pod. The
/// control instance logged it once per pod on each of 8 pod generations in 24 hours.
/// <see cref="UserDirectoryReadiness"/> now opens the directory when the application starts and
/// holds readiness until the first snapshot lands.</para>
///
/// <para>The directory provider HOLDS its snapshot until the test releases it. That makes "the
/// snapshot has not arrived" a state the test controls, not a race it has to win.</para>
/// </summary>
public class UserDirectoryOpensAtStartTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string UserId = "start-probe-user";
    private const string Email = "start-probe@example.com";

    private readonly HeldDirectoryProvider provider = new(UserId, Email);

    /// <summary>How many more times resolving the directory THROWS — the open-failure leg.</summary>
    private int failingOpens;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .ConfigureServices(services => services
            .AddSingleton<IMeshQueryProvider>(provider)
            // Registered as the portal host registers it (MeshWeaver.Plugins,
            // BlazorHostingExtensions): a plain singleton, built on first resolve. The factory only
            // adds the switch that makes a resolve throw; the DI container does not cache a
            // factory's exception, so the next resolve builds the real instance.
            .AddSingleton<UserIdentityCache>(sp =>
                System.Threading.Interlocked.Decrement(ref failingOpens) >= 0
                    ? throw new InvalidOperationException("injected: the directory could not be built")
                    : ActivatorUtilities.CreateInstance<UserIdentityCache>(sp)));

    /// <summary>
    /// The host's view of the readiness service: the mesh hub plus the REAL generic-host lifetime,
    /// so ApplicationStarted has production semantics — registered callbacks run when
    /// <see cref="ApplicationLifetime.NotifyStarted"/> fires, and not before
    /// (pattern: <c>ModuleGcOffReadinessPathTest</c>).
    /// </summary>
    private (UserDirectoryReadiness Readiness, ApplicationLifetime Lifetime, ServiceProvider Host) StartableReadiness()
    {
        var lifetime = new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance);
        var host = new ServiceCollection()
            .AddSingleton<IMessageHub>(Mesh)
            .AddSingleton<IHostApplicationLifetime>(lifetime)
            .BuildServiceProvider();
        var readiness = new UserDirectoryReadiness(
            host,
            Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<UserDirectoryReadiness>());
        return (readiness, lifetime, host);
    }

    private static Task<HealthCheckResult> Probe(UserDirectoryReadiness readiness, CancellationToken ct)
        => readiness.CheckHealthAsync(new HealthCheckContext(), ct);

    [Fact(Timeout = 240_000)]
    public async Task ReadinessOpensTheDirectoryAtStart_AndTheFirstLookupIsHydrated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (readiness, lifetime, host) = StartableReadiness();
        await using var _ = host;

        await readiness.StartAsync(ct);

        // ── StartAsync runs BEFORE the host has started, so it only registers: the directory is
        //    not open, and a probe in this window does not open it either. (cache == null is what
        //    "not open yet" reports — deterministic, unlike counting reads on the query pool.)
        var beforeStart = await Probe(readiness, ct);
        beforeStart.Status.Should().Be(HealthStatus.Unhealthy);
        beforeStart.Description.Should().Contain("not open yet",
            "the directory must open on ApplicationStarted, never in StartAsync or on a probe before it");
        provider.DirectoryReads.Should().Be(0);

        lifetime.NotifyStarted();

        // ── The directory read is OPEN before any request has asked it. This is the fix: with the
        //    old shape nothing opened it until the first lookup.
        await Observable.Interval(TestTimeouts.Quick / 100).StartWith(0L)
            .Select(_ => provider.DirectoryReads)
            .Where(n => n >= 1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("ApplicationStarted must open the user directory read before any request asks it", ct);

        // ── Not ready while the first snapshot is held.
        (await Probe(readiness, ct)).Status
            .Should().Be(HealthStatus.Unhealthy, "a replica whose directory has no first snapshot cannot identify anyone");

        provider.Release();

        // ── Ready once it lands, and only then would the Service route a request here.
        // (Reading is a property read, not a lookup — so "the first lookup" below stays the first.)
        var cache = Mesh.ServiceProvider.GetRequiredService<UserIdentityCache>();
        await Observable.Interval(TestTimeouts.Quick / 100).StartWith(0L)
            .Select(_ => cache.Reading)
            .Where(r => r.EverHydrated)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the released snapshot must reach the directory", ct);
        (await Probe(readiness, ct)).Status
            .Should().Be(HealthStatus.Healthy, "the first snapshot turns readiness Healthy");

        // ── THE PROPERTY: the FIRST lookup this process makes is a verdict, not Unavailable.
        var first = cache.Lookup(Email);
        first.IsUnavailable.Should().BeFalse(
            $"the first lookup on a ready replica must meet a hydrated index, got: {first.UnavailableReason}");
        first.Node!.Id.Should().Be(UserId);
        provider.DirectoryReads.Should().Be(1, "the lookup asked the read readiness opened — not a second one");

        await readiness.StopAsync(ct);
    }

    /// <summary>
    /// An open that THROWS at ApplicationStarted is retried by the next probe — never left to keep
    /// the replica Unhealthy for the life of the process. Without the retry the second probe
    /// still reports "could not be opened" and no directory read is ever issued.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AnOpenThatThrows_IsRetriedByTheNextProbe_AndTheFailureClears()
    {
        var ct = TestContext.Current.CancellationToken;
        var (readiness, lifetime, host) = StartableReadiness();
        await using var _ = host;
        System.Threading.Volatile.Write(ref failingOpens, 1);

        await readiness.StartAsync(ct);
        lifetime.NotifyStarted();   // the open runs — and throws

        var failed = await Probe(readiness, ct);
        // This probe already retried (and the retry succeeded), so it must NOT report the stale
        // failure: the directory is open and waiting for its first snapshot.
        failed.Description.Should().NotContain("could not be opened",
            "a probe after ApplicationStarted must retry the failed open and clear its failure on success");
        failed.Status.Should().Be(HealthStatus.Unhealthy, "the directory is open but has no first snapshot yet");

        await Observable.Interval(TestTimeouts.Quick / 100).StartWith(0L)
            .Select(_ => provider.DirectoryReads)
            .Where(n => n >= 1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the retried open must issue the directory read", ct);

        provider.Release();
        var cache = Mesh.ServiceProvider.GetRequiredService<UserIdentityCache>();
        await Observable.Interval(TestTimeouts.Quick / 100).StartWith(0L)
            .Select(_ => cache.Reading)
            .Where(r => r.EverHydrated)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the released snapshot must reach the directory the retry opened", ct);
        (await Probe(readiness, ct)).Status.Should().Be(HealthStatus.Healthy);

        await readiness.StopAsync(ct);
    }

    /// <summary>
    /// An INCOMPLETE first snapshot (a provider dropped part of its read) must not hydrate the
    /// directory or release readiness: users may be missing, and a miss would be a false
    /// "no such user". It is recorded as the read's failure, and the next probe opens a fresh read
    /// whose complete snapshot does hydrate it. Without the rejection the floor hydrates the index,
    /// no failure is ever recorded, and the wait below for one times out.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AnIncompleteFirstSnapshot_DoesNotHydrate_AndTheNextProbeReopens()
    {
        var ct = TestContext.Current.CancellationToken;
        var (readiness, lifetime, host) = StartableReadiness();
        await using var _ = host;
        provider.IncompleteReads = 1;
        provider.Release();

        await readiness.StartAsync(ct);
        lifetime.NotifyStarted();

        var cache = Mesh.ServiceProvider.GetRequiredService<UserIdentityCache>();
        var rejected = await Observable.Interval(TestTimeouts.Quick / 100).StartWith(0L)
            .Select(_ => cache.Reading)
            .Where(r => r.Failure is not null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        rejected.Failure.Should().Contain("INCOMPLETE");
        rejected.EverHydrated.Should().BeFalse("a floor must never count as the first snapshot");
        rejected.Hydrated.Should().BeFalse();

        // A probe re-opens the terminated read; the replacement's snapshot is complete. The provider
        // is already released, so the replacement may hydrate before this probe samples. Its verdict
        // is therefore not asserted (it only triggers the re-open); the `rejected` assertions above
        // already prove the incomplete snapshot did not hydrate.
        await Probe(readiness, ct);
        await Observable.Interval(TestTimeouts.Quick / 100).StartWith(0L)
            .Select(_ => cache.Reading)
            .Where(r => r.EverHydrated)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the probe must re-open the read and its complete snapshot must hydrate the directory", ct);
        provider.DirectoryReads.Should().Be(2, "one rejected read, one replacement opened by the probe");
        (await Probe(readiness, ct)).Status.Should().Be(HealthStatus.Healthy);
        cache.Lookup(Email).Node!.Id.Should().Be(UserId);

        await readiness.StopAsync(ct);
    }

    /// <summary>
    /// NEGATIVE CONTROL — the shape before the fix: no readiness, so the first request builds the
    /// directory and asks it in the same breath. That first lookup is Unavailable. The snapshot is
    /// held, so this is deterministic and not a lost race.
    /// </summary>
    [Fact]
    public void WithoutReadiness_TheFirstLookupBuildsTheDirectoryAndMeetsItCold()
    {
        provider.DirectoryReads.Should().Be(0);

        var cache = Mesh.ServiceProvider.GetRequiredService<UserIdentityCache>();
        var first = cache.Lookup(Email);

        first.IsUnavailable.Should().BeTrue("the request that opens the directory always meets it before its first snapshot");
        first.UnavailableReason.Should().Contain("first snapshot");
        provider.Release();
    }

    [Fact]
    public void Classify_IsUnreadyUntilTheFirstSnapshot_AndNeverAfter()
    {
        UserDirectoryReadiness.Classify(null, null, null).Status.Should().Be(HealthStatus.Unhealthy);
        UserDirectoryReadiness.Classify(new(false, false, null, 0), null, null).Status.Should().Be(HealthStatus.Unhealthy);
        UserDirectoryReadiness.Classify(new(false, false, "boom", 0), null, null).Status.Should().Be(HealthStatus.Unhealthy);
        UserDirectoryReadiness.Classify(null, null, "threw").Status.Should().Be(HealthStatus.Unhealthy);
        UserDirectoryReadiness.Classify(new(true, true, null, 3), null, null).Status.Should().Be(HealthStatus.Healthy);
        // Once it has answered, a rebuild or a terminated read is printed but holds no traffic.
        UserDirectoryReadiness.Classify(new(false, true, null, 3), null, null).Status.Should().Be(HealthStatus.Degraded);
        UserDirectoryReadiness.Classify(new(false, true, "boom", 3), null, null).Status.Should().Be(HealthStatus.Degraded);
        // A host with no directory has nothing to measure.
        UserDirectoryReadiness.Classify(null, "no directory", null).Status.Should().Be(HealthStatus.Healthy);
    }

    /// <summary>
    /// Answers the directory read (<c>nodeType:User</c>, exactly) with one user, but only once the
    /// test releases it; every other query gets a prompt empty Initial so the rest of the mesh is
    /// untouched.
    /// </summary>
    private sealed class HeldDirectoryProvider(string userId, string email) : IMeshQueryProvider
    {
        private readonly AsyncSubject<Unit> _release = new();
        private int _reads;
        private int _incompleteReads;

        /// <summary>How many directory reads (from the next one on) answer an INCOMPLETE snapshot.</summary>
        public int IncompleteReads { set => System.Threading.Volatile.Write(ref _incompleteReads, value); }

        public string Name => nameof(HeldDirectoryProvider);
        public int DirectoryReads => System.Threading.Volatile.Read(ref _reads);
        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

        public void Release()
        {
            _release.OnNext(Unit.Default);
            _release.OnCompleted();
        }

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
        {
            if (!request.EffectiveQueries.Any(q => string.Equals(q.Trim(), "nodeType:User", StringComparison.OrdinalIgnoreCase)))
                return Observable.Return(Frame<T>([]));
            System.Threading.Interlocked.Increment(ref _reads);
            if (System.Threading.Interlocked.Decrement(ref _incompleteReads) >= 0)
                // A provider that dropped part of its read: the frame carries NO user, which is
                // exactly the lie a floor would tell if it were applied.
                return _release.Select(_ => Frame<T>([]) with { SnapshotIncomplete = true })
                    .Concat(Observable.Never<QueryResultChange<T>>());
            var node = new MeshNode(userId)
            {
                Name = userId, NodeType = "User", State = MeshNodeState.Active,
                Content = new MeshWeaver.Mesh.Security.User { Email = email, FullName = userId },
            };
            return _release.Select(_ => Frame<T>([(T)(object)node])).Concat(Observable.Never<QueryResultChange<T>>());
        }

        private static QueryResultChange<T> Frame<T>(T[] items) => new()
        {
            ChangeType = QueryChangeType.Initial,
            Items = items,
            Timestamp = DateTimeOffset.UtcNow,
        };

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
