using System.Reactive.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// 🚨 <b>The periodic fallback for a lost push delivery</b> (policy <c>sources-sync-on-push</c>;
/// <c>Doc/Architecture/SourcesSyncOnPush</c>).
///
/// <para>Sources sync on the <c>push</c> webhook. GitHub does not redeliver a failed delivery by
/// itself, so a push whose webhook never reached this instance — a pod restarting, an ingress
/// hiccup, a hook briefly misconfigured — would otherwise leave every Space of that repository on
/// its old commit until somebody pushed again. Every <see cref="IntervalConfigKey"/> minutes this
/// asks <see cref="GitHubWebhookProcessor.ReconcileBranches"/> to resolve each configured branch's
/// head (one ref lookup per repository branch) and import where a source is not on it.</para>
///
/// <para><b>Why a timer is not a band-aid here.</b> It does not paper over a state that "should not
/// happen" inside the mesh: a webhook is an at-most-once delivery from a system this instance does
/// not control, and the ONLY way to learn that a delivery was lost is to ask the source of truth.
/// The pass is idempotent — a source already on the head, or settled at it with a final verdict,
/// costs the ref lookup and nothing else — and it never retries a failed import of its own: the
/// next pass simply asks again.</para>
///
/// <para>Serialised (<c>Concat</c>): a pass that outlives the interval delays the next one rather
/// than overlapping it. A faulted pass is logged and does not tear the schedule down.</para>
/// </summary>
internal sealed class GitSyncBranchReconcileService(
    GitHubWebhookProcessor processor,
    IConfiguration? configuration = null,
    ILogger<GitSyncBranchReconcileService>? logger = null) : IHostedService, IDisposable
{
    /// <summary>Configuration key: minutes between two reconcile passes.</summary>
    public const string IntervalConfigKey = "GitSync:BranchReconcileMinutes";

    /// <summary>The interval when nothing is configured. Long enough that one pass per repository
    /// branch is negligible against GitHub's rate limit, short enough that a lost delivery costs
    /// minutes rather than the 15 h the green-build trigger once cost.</summary>
    public const int DefaultIntervalMinutes = 10;

    private IDisposable? schedule;

    /// <summary>The configured interval — the default for an absent, unparsable or non-positive value.</summary>
    internal TimeSpan Interval =>
        int.TryParse(configuration?[IntervalConfigKey], out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.FromMinutes(DefaultIntervalMinutes);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var interval = Interval;
        schedule = Observable.Timer(interval, interval)
            .Select(_ => processor.ReconcileBranches()
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex,
                        "[GitSync] branch reconcile pass faulted — the next pass in {Interval} asks again.",
                        interval);
                    return Observable.Return(0);
                }))
            .Concat()
            .Subscribe(
                triggered =>
                {
                    if (triggered > 0)
                        logger?.LogInformation(
                            "[GitSync] branch reconcile triggered {Count} import(s) — a push delivery for "
                            + "them never arrived or never landed.", triggered);
                },
                ex => logger?.LogWarning(ex,
                    "[GitSync] the branch reconcile schedule faulted — lost push deliveries are no longer "
                    + "repaired until the next restart."));
        logger?.LogInformation("[GitSync] branch reconcile armed: every {Interval}.", interval);
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
