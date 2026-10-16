using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.AspNetCore.Portal;

/// <summary>
/// One consistent reading of the portal's user directory (<see cref="UserIdentityCache"/>).
/// </summary>
/// <param name="Hydrated">A snapshot is applied right now, so a miss is a definitive "no such user".</param>
/// <param name="EverHydrated">A snapshot has been applied at least once in this process.</param>
/// <param name="Failure">Why the directory read terminated, or <c>null</c> while it is live.</param>
/// <param name="IndexedUsers">How many users the index holds by email.</param>
public readonly record struct UserDirectoryReading(
    bool Hydrated, bool EverHydrated, string? Failure, int IndexedUsers);

/// <summary>
/// 🚦 <b>Opens the user directory when the application starts, and holds readiness until it has
/// received its first snapshot</b> (MeshWeaver#6850).
///
/// <para><b>The defect it removes.</b> <see cref="UserIdentityCache"/> is a DI singleton, and a
/// singleton is built the first time something resolves it. The only things that resolved it were
/// the request paths (<c>UserContextMiddleware</c>, <c>ResolveHttpCaller</c>,
/// <c>CircuitAccessHandler</c>), so the directory read was opened BY the first signed-in request,
/// and that same request then asked the index it had just created. The read subscribes on the
/// query pool, so the answer was "the mesh user index has not received its first snapshot yet" on
/// every pod, by construction. That is one degraded request per pod, and it is what
/// the control instance logged once per pod on each of 8 pod generations in 24 hours.</para>
///
/// <para><b>What it does.</b> <see cref="StartAsync"/> resolves the cache when the host reports
/// <see cref="IHostApplicationLifetime.ApplicationStarted"/>, so the read is open from boot and
/// never later than the first request could have opened it. Waiting for ApplicationStarted, and not
/// opening the read inside StartAsync, keeps the read after every other hosted service has started.
/// An earlier read could see a partial first snapshot, and a partial snapshot answers a definitive
/// "no such user" (#974). The health check then reports Unhealthy until the first snapshot has
/// been applied. It is registered as a ROLL GATE (<c>user_directory</c>, see
/// <c>ServiceDefaults.RollGateChecks</c>), so a pod that cannot identify anyone stays alive and out
/// of the Service, and the startup probe never kills a container for it.</para>
///
/// <para><b>After the first snapshot it never takes the pod out of rotation again.</b> A directory
/// read that later terminates is re-opened by the cache's next lookup, and every lookup in between
/// already answers Unavailable. That state reads Degraded: it is printed, and it holds nothing.
/// Pulling every replica because the shared directory read stalled would turn degraded identity
/// into an outage.</para>
///
/// <para>A host that registers no <see cref="UserIdentityCache"/> (a migration job, a setup-only
/// boot, a test host) has nothing to measure and reports Healthy with that said.</para>
/// </summary>
public sealed class UserDirectoryReadiness(IServiceProvider services, ILogger<UserDirectoryReadiness> logger)
    : IHostedService, IHealthCheck
{
    /// <summary>The health check's registration name.</summary>
    public const string HealthCheckName = "user_directory";

    private UserIdentityCache? cache;
    private string? absent;
    private string? openFailure;
    private CancellationTokenRegistration startedRegistration;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var lifetime = services.GetService<IHostApplicationLifetime>();
        if (lifetime is null)
            Open();
        else
            startedRegistration = lifetime.ApplicationStarted.Register(Open);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        startedRegistration.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves the cache through the mesh hub's provider, the same provider every request path
    /// resolves it through, so this opens the instance those paths will ask.
    /// </summary>
    private void Open()
    {
        try
        {
            var hub = services.GetService<IMessageHub>();
            if (hub is null)
            {
                Volatile.Write(ref absent, "no mesh hub in this host — nothing to measure");
                return;
            }
            var resolved = hub.ServiceProvider.GetService<UserIdentityCache>();
            if (resolved is null)
            {
                Volatile.Write(ref absent, "this host registers no user directory — nothing to measure");
                return;
            }
            Volatile.Write(ref cache, resolved);
            logger.LogInformation(
                "User directory opened at application start; readiness waits for its first snapshot");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref openFailure, $"{ex.GetType().Name}: {ex.Message}");
            logger.LogWarning(ex, "User directory could not be opened at application start");
        }
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var current = Volatile.Read(ref cache);
        current?.EnsureOpen();
        return Task.FromResult(Classify(
            current?.Reading, Volatile.Read(ref absent), Volatile.Read(ref openFailure)));
    }

    /// <summary>
    /// The readiness verdict, pure so every leg is tested without a mesh.
    /// </summary>
    /// <param name="reading">The directory's reading, or <c>null</c> when it is not open (yet).</param>
    /// <param name="absent">Why this host has nothing to measure, or <c>null</c>.</param>
    /// <param name="openFailure">Why opening the directory threw, or <c>null</c>.</param>
    /// <returns>Unhealthy until the first snapshot; Degraded while a directory that has answered
    /// before is being re-read; Healthy otherwise.</returns>
    public static HealthCheckResult Classify(UserDirectoryReading? reading, string? absent, string? openFailure)
    {
        if (absent is not null)
            return HealthCheckResult.Healthy(absent);
        if (openFailure is not null)
            return HealthCheckResult.Unhealthy(
                $"the user directory could not be opened ({openFailure}) — this replica cannot identify a signed-in user");
        if (reading is not { } r)
            return HealthCheckResult.Unhealthy(
                "the user directory is not open yet (it opens when the application has started) — not ready");
        if (!r.EverHydrated)
            return HealthCheckResult.Unhealthy(r.Failure is null
                ? "the user directory has not received its first snapshot yet — not ready until it has"
                : $"the user directory failed before its first snapshot ({r.Failure}) — not ready; the next lookup re-opens it");
        if (!r.Hydrated)
            return HealthCheckResult.Degraded(r.Failure is null
                ? $"the user directory is being rebuilt; lookups answer Unavailable until it lands (last snapshot held {r.IndexedUsers} user(s))"
                : $"the user directory read terminated ({r.Failure}); the next lookup re-opens it");
        return HealthCheckResult.Healthy($"user directory hydrated: {r.IndexedUsers} user(s) indexed by email");
    }
}
