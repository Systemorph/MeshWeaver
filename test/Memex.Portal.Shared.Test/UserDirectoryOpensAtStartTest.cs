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
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
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

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .ConfigureServices(services => services
            .AddSingleton<IMeshQueryProvider>(provider)
            // Registered exactly as the portal host registers it (MeshWeaver.Plugins,
            // BlazorHostingExtensions): a plain singleton, built on first resolve.
            .AddSingleton<UserIdentityCache>());

    [Fact(Timeout = 240_000)]
    public async Task ReadinessOpensTheDirectoryAtStart_AndTheFirstLookupIsHydrated()
    {
        var ct = TestContext.Current.CancellationToken;
        var readiness = new UserDirectoryReadiness(
            Mesh.ServiceProvider,
            Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<UserDirectoryReadiness>());

        provider.DirectoryReads.Should().Be(0, "nothing has opened the directory before the host starts");

        await readiness.StartAsync(ct);

        // ── The directory read is OPEN before any request has asked it. This is the fix: with the
        //    old shape nothing opened it until the first lookup.
        await Observable.Interval(TestTimeouts.Quick / 100).StartWith(0L)
            .Select(_ => provider.DirectoryReads)
            .Where(n => n >= 1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("starting the host must open the user directory read before any request asks it", ct);

        // ── Not ready while the first snapshot is held.
        (await readiness.CheckHealthAsync(new HealthCheckContext(), ct)).Status
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
        (await readiness.CheckHealthAsync(new HealthCheckContext(), ct)).Status
            .Should().Be(HealthStatus.Healthy, "the first snapshot turns readiness Healthy");

        // ── THE PROPERTY: the FIRST lookup this process makes is a verdict, not Unavailable.
        var first = cache.Lookup(Email);
        first.IsUnavailable.Should().BeFalse(
            $"the first lookup on a ready replica must meet a hydrated index, got: {first.UnavailableReason}");
        first.Node!.Id.Should().Be(UserId);
        provider.DirectoryReads.Should().Be(1, "the lookup asked the read readiness opened — not a second one");
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
