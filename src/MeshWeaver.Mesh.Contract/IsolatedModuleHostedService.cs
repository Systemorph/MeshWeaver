using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Wraps a hosted service a MODULE contributed, so that failing to activate or start it costs that
/// module's feature rather than the whole portal (#2449).
///
/// <para>🚨 <b>Registering a service is not resolving it.</b> <c>MeshBuilder.InstallAssemblies</c>
/// already isolates installation per module (#2234) — assembly load, attribute materialisation,
/// the <c>GlobalServiceConfigurations</c> invocation and the builder fold. But a module adding an
/// <c>IHostedService</c> only registers a DESCRIPTOR there; nothing throws. The constructor runs
/// later, when the generic host resolves <c>IHostedService[]</c> during startup — outside
/// <c>InstallAssemblies</c> entirely — and Autofac resolves that array all-or-nothing. One
/// unsatisfiable constructor aborted the whole host.</para>
///
/// <para>Measured on memex-cloud 2026-08-26: a landed <c>MeshWeaver.AI.OpenAI</c> built against a
/// core newer than the running image registered <c>OpenAICompatibleModelSync</c>; its constructor
/// could not be satisfied; every replacement pod aborted at boot with SIGABRT and the rollout
/// wedged for hours while the old ReplicaSet kept serving. A binary skew between image and landed
/// modules is EXPECTED transiently — that is why per-module isolation exists — and with activation
/// unprotected it turned a one-feature problem into a portal that could not start.</para>
///
/// <para>🚨 <b>This is not a blanket catch around host startup, and must never become one.</b> A
/// hosted service the PLATFORM registers failing to activate is still fatal, deliberately. Only
/// registrations made while a module's own configuration was running are wrapped — the same
/// scoping installation isolation already uses.</para>
/// </summary>
internal sealed class IsolatedModuleHostedService(
    string moduleName,
    Func<IServiceProvider, object> resolve,
    IServiceProvider services,
    ILogger? logger) : IHostedService
{
    private IHostedService? inner;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            inner = resolve(services) as IHostedService;
        }
        catch (Exception ex)
        {
            Report(ex, "could not be ACTIVATED");
            return Task.CompletedTask;
        }

        if (inner is null)
            return Task.CompletedTask;

        try
        {
            // The Task boundary is the framework's, not ours: IHostedService is Task-shaped, so a
            // fault can arrive either synchronously or on the returned task. Both are isolated —
            // catching only the synchronous half would leave the host abortable by the other.
            return inner.StartAsync(cancellationToken)
                .ContinueWith(
                    t =>
                    {
                        if (t.IsFaulted)
                            Report(t.Exception, "FAILED TO START");
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Report(ex, "FAILED TO START");
            return Task.CompletedTask;
        }
    }

    /// <summary>Stops the inner service when it actually started; a module that never activated has
    /// nothing to stop, and must not make shutdown fail either.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (inner is null)
            return Task.CompletedTask;
        try
        {
            return inner.StopAsync(cancellationToken)
                .ContinueWith(
                    t =>
                    {
                        if (t.IsFaulted)
                            Report(t.Exception, "failed to STOP");
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Report(ex, "failed to STOP");
            return Task.CompletedTask;
        }
    }

    private void Report(Exception? ex, string what)
    {
        logger?.LogError(ex,
            "Module '{Module}' contributed a hosted service that {What} — the portal continues "
            + "WITHOUT that module's contribution. This is usually a binary skew between the image "
            + "and a landed module; check the module's build against the running platform.",
            moduleName, what);
        // The same last-resort channel InstallAssemblies uses: at this point in startup the
        // logging pipeline may not be serving, and a silent skip is the outcome this exists to
        // prevent being invisible.
        Console.Error.WriteLine(
            $"[MeshWeaver.Mesh.IncompatibleModule] hosted service from '{moduleName}' {what}: {ex?.Message}");
    }
}
