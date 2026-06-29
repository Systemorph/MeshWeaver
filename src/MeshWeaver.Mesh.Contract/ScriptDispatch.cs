using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Reusable static helper that maps a request / response pair onto the
/// activity + script-execution control plane <b>without ever awaiting the
/// activity</b>. The shape is fire-and-observe:
///
/// <list type="number">
///   <item>Caller posts <c>FooRequest</c> at the FooHandler.</item>
///   <item>FooHandler builds an <see cref="ExecuteScriptRequest"/> with caller-supplied
///         <see cref="ExecuteScriptRequest.Inputs"/> and dispatches it at a Code
///         template MeshNode (e.g. <c>Templates/Export/Pdf</c>).</item>
///   <item>The kernel creates an <c>Activity</c> MeshNode, the dispatch returns
///         immediately with the activity path.</item>
///   <item>FooHandler posts <c>FooResponse</c> back as a <c>ResponseFor</c> the
///         original delivery, carrying the activity path. <b>The handler does
///         NOT subscribe to the activity stream</b> — that's the caller's job.</item>
///   <item>Caller subscribes to the activity stream
///         (<c>workspace.GetMeshNodeStream(activityPath)</c>) for live
///         <c>Messages</c> progress + the script's terminal
///         <see cref="MeshWeaver.Data.ActivityLog.ReturnValue"/>.</item>
/// </list>
///
/// <para>This is the canonical "operations as scripts" relay pattern — see
/// <c>Doc/Architecture/ActivityControlPlane.md</c> → "Operations as scripts".
/// The just-start shape (NOT wait-for-terminal) is required because
/// the handler runs on the mesh hub's action block; if it sat there waiting
/// for the activity to finish, the action block would be busy with the
/// script's own cross-hub <c>CreateNode</c> traffic and the subscription
/// callback would queue behind that work, deadlocking the relay (see
/// <c>Doc/Architecture/AsynchronousCalls.md</c> → "🚨🚨🚨 NOTHING ASYNC EVER").</para>
///
/// <para>Stateless static helper, per the static-handlers rule
/// (<c>Doc/Architecture/AsynchronousCalls.md</c> → "Static handlers compose").</para>
/// </summary>
public static class ScriptDispatch
{
    /// <summary>
    /// Relay a request delivery to a Code template script and post a
    /// <i>start-acknowledgement</i> response back as soon as the kernel
    /// returns the activity path. The caller observes the activity stream
    /// for progress and the script's return value.
    /// </summary>
    /// <typeparam name="TRequest">Inbound request type. Must implement
    /// <see cref="IRequest{TResponse}"/> so <c>ResponseFor</c> routes the
    /// response correctly.</typeparam>
    /// <typeparam name="TResponse">Outbound response type. Built from the
    /// activity path via <paramref name="mapStarted"/> on success or from
    /// the failure reason via <paramref name="mapFailure"/>.</typeparam>
    /// <param name="hub">The handling hub. Used to <c>Observe</c> the
    /// <see cref="ExecuteScriptRequest"/> and post the eventual response.</param>
    /// <param name="delivery">The inbound request delivery. The relay marks
    /// it <c>Processed()</c> on return and uses <c>ResponseFor(delivery)</c>
    /// when posting the eventual response.</param>
    /// <param name="templatePath">Mesh path of the Code MeshNode template to
    /// execute (e.g. <c>Templates/Export/Pdf</c>).</param>
    /// <param name="inputs">Caller-supplied inputs forwarded to the script as
    /// the <c>Inputs</c> global. Empty for parameterless templates.</param>
    /// <param name="mapStarted">Maps the activity path (and submission id)
    /// to the success response shape. Called as soon as the kernel returns
    /// <see cref="ExecuteScriptResponse.Success"/> = true.</param>
    /// <param name="mapFailure">Maps a dispatch-time failure reason to a
    /// failure response. Only called when the kernel rejects the dispatch
    /// (<c>IsExecutable</c> = false, permission denied, …); script-time
    /// failures surface to subscribers via <c>ActivityLog.Status = Failed</c>.</param>
    /// <param name="logger">Optional logger for relay-side diagnostics.</param>
    public static IMessageDelivery StartScript<TRequest, TResponse>(
        IMessageHub hub,
        IMessageDelivery<TRequest> delivery,
        string templatePath,
        ImmutableDictionary<string, JsonElement> inputs,
        Func<ScriptDispatchStarted, TResponse> mapStarted,
        Func<string, TResponse> mapFailure,
        ILogger? logger = null)
        where TRequest : IRequest<TResponse>
    {
        var execRequest = new ExecuteScriptRequest { Inputs = inputs };

        hub.Observe<ExecuteScriptResponse>(
                execRequest,
                o => o.WithTarget(new Address(templatePath)))
            .Take(1)
            .Subscribe(
                execResp =>
                {
                    var msg = execResp.Message;
                    if (msg.Success && !string.IsNullOrEmpty(msg.ActivityLog))
                    {
                        var started = new ScriptDispatchStarted(
                            msg.ActivityLog!,
                            msg.SubmissionId ?? "");
                        hub.Post(mapStarted(started)!, o => o.ResponseFor(delivery));
                    }
                    else
                    {
                        var reason = msg.Error ?? $"Script dispatch failed at {templatePath}";
                        logger?.LogError("Script dispatch {Template} failed: {Reason}",
                            templatePath, reason);
                        hub.Post(mapFailure(reason)!, o => o.ResponseFor(delivery));
                    }
                },
                ex =>
                {
                    logger?.LogError(ex, "Script dispatch {Template} faulted for delivery {Id}",
                        templatePath, delivery.Id);
                    hub.Post(mapFailure($"{templatePath}: {ex.Message}")!, o => o.ResponseFor(delivery));
                });

        return delivery.Processed();
    }
}

/// <summary>
/// Result of a successful <see cref="ScriptDispatch.StartScript{TRequest,TResponse}"/>:
/// the activity path callers subscribe to and the kernel's submission id.
/// </summary>
/// <param name="ActivityPath">Mesh path of the <c>Activity</c> MeshNode the
/// script is running on. Subscribe via
/// <c>workspace.GetMeshNodeStream(ActivityPath)</c> for live progress and the
/// terminal <see cref="MeshWeaver.Data.ActivityLog.ReturnValue"/>.</param>
/// <param name="SubmissionId">Kernel-assigned submission identifier; useful for
/// correlating layout-area output panes.</param>
public sealed record ScriptDispatchStarted(string ActivityPath, string SubmissionId);
