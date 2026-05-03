using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Reusable static helpers that map a request / response pair onto the
/// activity + script-execution control plane. The shape:
///
/// <list type="number">
///   <item>Caller posts <c>FooRequest</c> at the FooHandler.</item>
///   <item>FooHandler builds an <see cref="ExecuteScriptRequest"/> with caller-supplied
///         <see cref="ExecuteScriptRequest.Inputs"/> and dispatches it at a Code
///         template MeshNode (e.g. <c>Templates/Foo/Bar</c>).</item>
///   <item>The kernel creates an <c>Activity</c> MeshNode, runs the script, writes
///         <c>Log.LogInformation</c> calls onto <c>ActivityLog.Messages</c>, and
///         records the script's <c>return</c> value as
///         <see cref="ActivityLog.ReturnValue"/> on the terminal snapshot.</item>
///   <item>The relay subscribes to the activity stream until terminal status,
///         deserialises <see cref="ActivityLog.ReturnValue"/> into <c>FooResponse</c>,
///         and posts it as a <c>ResponseFor</c> the original delivery.</item>
/// </list>
///
/// <para>This is the canonical "operations as scripts" relay — every request /
/// response pair backed by a script template (export, import, generate, …)
/// uses this helper to avoid hand-rolling the activity-subscription pipeline
/// per handler. See <c>Doc/Architecture/ActivityControlPlane.md</c> →
/// "Operations as scripts".</para>
///
/// <para>Stateless static helper, per the static-handlers rule
/// (<c>Doc/Architecture/AsynchronousCalls.md</c> → "Static handlers compose").</para>
/// </summary>
public static class ScriptDispatch
{
    /// <summary>
    /// Relay a request delivery through a Code template script and post the
    /// mapped response back to the original sender. Returns
    /// <c>delivery.Processed()</c> immediately — the activity round-trip runs
    /// in the background via <c>Subscribe</c>; no <c>await</c>, no Task.
    /// </summary>
    /// <typeparam name="TRequest">Inbound request type. Must implement
    /// <see cref="IRequest{TResponse}"/> so <c>ResponseFor</c> routes the
    /// response correctly.</typeparam>
    /// <typeparam name="TResponse">Outbound response type. Composed from the
    /// activity's <see cref="ActivityLog.ReturnValue"/> via <paramref name="mapSuccess"/>
    /// or from the failure reason via <paramref name="mapFailure"/>.</typeparam>
    /// <param name="hub">The handling hub. Used to <c>Observe</c> the
    /// <see cref="ExecuteScriptRequest"/>, subscribe to the activity stream,
    /// and post the final response.</param>
    /// <param name="delivery">The inbound request delivery. The relay marks
    /// it <c>Processed()</c> on return and uses <c>ResponseFor(delivery)</c>
    /// when posting the eventual response.</param>
    /// <param name="templatePath">Mesh path of the Code MeshNode template to
    /// execute (e.g. <c>Templates/Export/Pdf</c>).</param>
    /// <param name="inputs">Caller-supplied inputs forwarded to the script as
    /// the <c>Inputs</c> global. Empty for parameterless templates.</param>
    /// <param name="mapSuccess">Maps the activity's
    /// <see cref="ActivityLog.ReturnValue"/> (possibly <c>null</c>) to the
    /// success response. Called when the activity terminates with
    /// <see cref="ActivityStatus.Succeeded"/>.</param>
    /// <param name="mapFailure">Maps a failure reason (cancellation, script
    /// exception, dispatch error, terminal <see cref="ActivityStatus.Failed"/>)
    /// to a failure response.</param>
    /// <param name="logger">Optional logger for relay-side diagnostics.
    /// Faults inside the activity stream are reported here; script-side
    /// failures are surfaced via <paramref name="mapFailure"/>.</param>
    /// <param name="timeout">Optional overall timeout. Defaults to 5 minutes —
    /// long enough for typical export / import work, short enough that a wedged
    /// activity surfaces as a failure rather than hanging the caller.</param>
    public static IMessageDelivery RelayToScript<TRequest, TResponse>(
        IMessageHub hub,
        IMessageDelivery<TRequest> delivery,
        string templatePath,
        ImmutableDictionary<string, JsonElement> inputs,
        Func<JsonElement?, TResponse> mapSuccess,
        Func<string, TResponse> mapFailure,
        ILogger? logger = null,
        TimeSpan? timeout = null)
        where TRequest : IRequest<TResponse>
    {
        var execRequest = new ExecuteScriptRequest { Inputs = inputs };

        hub.Observe<ExecuteScriptResponse>(
                execRequest,
                o => o.WithTarget(new Address(templatePath)))
            .Take(1)
            .SelectMany(execResp =>
            {
                var msg = execResp.Message;
                if (!msg.Success || string.IsNullOrEmpty(msg.ActivityLog))
                    return Observable.Throw<ActivityLog>(
                        new InvalidOperationException(
                            msg.Error ?? $"Script dispatch failed at {templatePath}"));

                return hub.GetWorkspace()
                    .GetMeshNodeStream(msg.ActivityLog!)
                    .Select(n => n?.Content as ActivityLog)
                    .Where(log => log is not null && log.Status != ActivityStatus.Running)
                    .Take(1)
                    .Select(log => log!);
            })
            .Timeout(timeout ?? TimeSpan.FromMinutes(5))
            .Subscribe(
                log =>
                {
                    if (log.Status == ActivityStatus.Succeeded)
                    {
                        var response = mapSuccess(log.ReturnValue);
                        hub.Post(response!, o => o.ResponseFor(delivery));
                    }
                    else
                    {
                        var reason = ExtractFailureReason(log)
                                     ?? $"Script terminated with status {log.Status}";
                        hub.Post(mapFailure(reason)!, o => o.ResponseFor(delivery));
                    }
                },
                ex =>
                {
                    logger?.LogError(ex, "Script relay {Template} faulted for delivery {Id}",
                        templatePath, delivery.Id);
                    hub.Post(mapFailure(ex.Message)!, o => o.ResponseFor(delivery));
                });

        return delivery.Processed();
    }

    private static string? ExtractFailureReason(ActivityLog log)
    {
        var firstError = log.Messages.FirstOrDefault(m => m.LogLevel == LogLevel.Error)?.Message;
        if (!string.IsNullOrEmpty(firstError)) return firstError;
        var firstWarning = log.Messages.FirstOrDefault(m => m.LogLevel == LogLevel.Warning)?.Message;
        return firstWarning;
    }
}
