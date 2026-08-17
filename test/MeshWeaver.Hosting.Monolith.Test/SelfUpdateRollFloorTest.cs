using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the pacing floor (#1778): how often this install may roll ITSELF.
///
/// <para>Since the check became event-driven a publication is a roll and a roll is a pod restart,
/// so without a floor publication frequency IS restart frequency — and every restart drops the
/// live circuits of everyone on the portal. The floor bounds the AUTOMATIC cadence only; an
/// operator still has <c>kubectl rollout restart</c>.</para>
///
/// <para>🚨 The case that decides the design is CRASH RECOVERY. The floor needs state that
/// survives a restart, because a successful roll restarts the process — so "last rolled" cannot be
/// held in memory. Process uptime is the tempting substitute and is WRONG: a pod that comes back
/// on an OLD image has a young process but an old deployment and would wait out a floor it has
/// long since satisfied. The stamp lives on the Deployment, so an old stamp frees it immediately.
/// <see cref="AnOldStampRollsImmediately_TheCrashRecoveryCase"/> is that test.</para>
/// </summary>
public class SelfUpdateRollFloorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddUpdatePolicyType().AddGitHubSyncTypes();

    private const string CandidateTag = "3.0.0";

    /// <summary>Updater whose "last rolled" stamp the test controls — the Deployment annotation's
    /// stand-in, and the only thing the floor reads.</summary>
    private sealed class StampedUpdater(DateTimeOffset? lastRolled) : IDeploymentUpdater
    {
        private readonly ReplaySubject<string> _patched = new();
        private ImmutableList<string> _tags = ImmutableList<string>.Empty;

        public IObservable<string> Patched => _patched;
        public ImmutableList<string> Tags => _tags;
        public bool CanPatch => true;

        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) =>
            Task.FromResult(lastRolled);

        public Task PatchToVersionAsync(string versionTag, CancellationToken ct)
        {
            ImmutableInterlocked.Update(ref _tags, tags => tags.Add(versionTag));
            _patched.OnNext(versionTag);
            return Task.CompletedTask;
        }
    }

    private sealed class OneTagLister : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([CandidateTag]);
    }

    private static SelfUpdateOptions Options(TimeSpan floor) => new()
    {
        RetryInterval = TimeSpan.FromMilliseconds(500),
        EventCoalesceWindow = TimeSpan.FromMilliseconds(50),
        MinRollInterval = floor,
        DefaultPolicy = UpdatePolicyKind.Continuous,
    };

    private async Task<ImmutableList<string>> RunStartupPassAsync(StampedUpdater updater, TimeSpan floor)
    {
        var service = new SelfUpdateHostedService(
            Mesh, new OneTagLister(), updater, Options(floor),
            Mesh.ServiceProvider.GetService<ILogger<SelfUpdateHostedService>>());
        await service.StartAsync(CancellationToken.None);
        try
        {
            // Give the startup pass room to reach the roll decision either way. A patch that is
            // going to happen has happened well inside this; a deferral never will.
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            return updater.Tags;
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task AFreshRoll_DefersTheNextOne()
    {
        var updater = new StampedUpdater(DateTimeOffset.UtcNow.AddMinutes(-5));
        var tags = await RunStartupPassAsync(updater, floor: TimeSpan.FromHours(1));
        tags.Should().BeEmpty(
            "the install rolled 5 minutes ago and the floor is an hour — rolling again would restart "
            + "the pod and drop every live circuit for a version it just took");
    }

    [Fact(Timeout = 60000)]
    public async Task AnOldStampRollsImmediately_TheCrashRecoveryCase()
    {
        // The shape process uptime gets wrong: a pod that has just started (young process) on an
        // image rolled long ago (old stamp) must roll AT ONCE, not wait out the floor.
        var updater = new StampedUpdater(DateTimeOffset.UtcNow.AddHours(-9));
        var tags = await RunStartupPassAsync(updater, floor: TimeSpan.FromHours(1));
        tags.Should().Equal([CandidateTag],
            "the last roll is older than the floor, so a freshly-restarted pod on an old image "
            + "must take the update immediately rather than waiting");
    }

    [Fact(Timeout = 60000)]
    public async Task NeverRolled_IsNotHeld()
    {
        // A first-ever roll has no stamp to compare against. It must not be held: an install that
        // has never self-updated is the one most in need of the update.
        var updater = new StampedUpdater(null);
        var tags = await RunStartupPassAsync(updater, floor: TimeSpan.FromHours(1));
        tags.Should().Equal([CandidateTag]);
    }

    [Fact(Timeout = 60000)]
    public async Task AZeroFloor_NeverHolds()
    {
        // The opt-out an install with no restart cost (a single-user dev portal) can set.
        var updater = new StampedUpdater(DateTimeOffset.UtcNow.AddSeconds(-1));
        var tags = await RunStartupPassAsync(updater, floor: TimeSpan.Zero);
        tags.Should().Equal([CandidateTag]);
    }
}
