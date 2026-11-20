using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Retention;

/// <summary>
/// Runs <see cref="DataRetentionSweep"/> on the instance policy's schedule: first after
/// <see cref="DataRetentionPolicy.InitialDelay"/>, then every <see cref="DataRetentionPolicy.Interval"/>
/// after the previous pass ENDED, so passes never overlap on one process.
///
/// <para><b>Every replica runs it, on purpose.</b> The plan is idempotent: two replicas compute
/// the same selection from the same store, and a row one of them already removed is an idempotent
/// delete for the other. The cost is the repeated window reads, which the long default interval
/// keeps small. No lease or lock is involved: nothing in the sweep's safety argument depends on
/// running once.</para>
/// </summary>
/// <param name="services">The mesh's service provider.</param>
/// <param name="logger">Where the pass lines go.</param>
public sealed class DataRetentionHostedService(
    IServiceProvider services,
    ILogger<DataRetentionHostedService> logger) : IHostedService, IDisposable
{
    private IDisposable? schedule;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var resolver = services.GetRequiredService<DataRetentionResolver>();
        var policy = resolver.Instance;
        if (!policy.Enabled)
        {
            logger.LogInformation("Data retention: the sweep is switched off ({Key} = false); nothing is pruned",
                DataRetentionPolicy.EnabledConfigKey);
            return Task.CompletedTask;
        }

        logger.LogInformation(
            "Data retention: first sweep in {Delay}, then every {Interval}. Activities {ActivitiesOn} after {ActivityAge}; "
            + "versions {VersionsOn} after {VersionAge} beyond the newest {KeepLast}; {LogType} rows {LogsOn} after {LogAge}; "
            + "{Rules} per-partition rule(s)",
            policy.InitialDelay, policy.Interval,
            policy.Activities.Enabled ? "on" : "OFF", policy.Activities.MaxAge,
            policy.Versions.Enabled ? "on" : "OFF", policy.Versions.MaxAge, policy.Versions.KeepLast,
            policy.LogEntries.NodeType, policy.LogEntries.Enabled ? "on" : "OFF", policy.LogEntries.MaxAge,
            resolver.Rules.Length);

        var pass = Observable.Defer(() =>
            {
                var hub = services.GetRequiredService<IMessageHub>().NodeOperationIssuingHub();
                return DataRetentionSweep.RunPass(hub, resolver, DateTimeOffset.UtcNow, logger);
            })
            .Select(_ => Unit.Default)
            // Inside the repeat: a faulted pass is reported and the schedule continues. Repeat
            // re-subscribes on completion only, so an error escaping here would end it for good.
            .Catch<Unit, Exception>(ex =>
            {
                PassLedger.ReportTerminal(DataRetentionSweep.PassName, logger, ex,
                    ": the pass faulted; whatever it removed before is logged per partition, and the next pass plans the rest");
                return Observable.Empty<Unit>();
            });

        schedule = Observable.Timer(policy.InitialDelay)
            .SelectMany(_ => pass
                .Concat(Observable.Timer(policy.Interval).Select(_ => Unit.Default).IgnoreElements())
                .Repeat())
            .Subscribe(_ => { }, ex => logger.LogWarning(ex, "Data retention: the schedule ended"));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        schedule?.Dispose();
        schedule = null;
    }
}

/// <summary>Registration of the data-retention sweep.</summary>
public static class DataRetentionExtensions
{
    /// <summary>
    /// Registers the instance policy (read once from configuration), the per-partition resolver
    /// (which picks up every registered <see cref="IDataRetentionRule"/>) and the scheduled sweep.
    /// Idempotent.
    /// </summary>
    /// <param name="services">The mesh's service collection.</param>
    public static IServiceCollection AddDataRetention(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => DataRetentionPolicy.FromConfiguration(sp.GetService<IConfiguration>()));
        services.TryAddSingleton(sp => new DataRetentionResolver(
            sp.GetRequiredService<DataRetentionPolicy>(), sp.GetServices<IDataRetentionRule>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DataRetentionHostedService>());
        return services;
    }
}
