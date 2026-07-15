using System;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Blazor view for <c>NamedAreaControl</c> — resolves an area reference to a
/// <c>UiControl</c> via the synchronisation stream and renders it, handling transient
/// hub failures, compilation-in-progress NACKs, and teardown gracefully.
/// </summary>
public partial class NamedAreaView
{
    /// <summary>
    /// When <c>true</c>, this area is the top-level root area of the page layout.
    /// Used to drive page-title and meta-attribute propagation.
    /// </summary>
    [Parameter] public bool Top { get; set; }

    private UiControl? RootControl { get; set; }


    private string? ProgressMessage;
    private bool ShowProgress;
    private SpinnerType SpinnerType;

    private IDisposable? subscription;
    private string? PageTitle;
    private IDictionary<string, object>? MetaAttributes;

    private string? AreaToBeRendered { get; set; }

    /// <summary>
    /// Tears down the prior area subscription and opens a new reactive subscription for
    /// the area reference. Applies bounded backoff retry for transient hub failures and
    /// swaps to the NodeType progress view when a CompilationInProgress NACK is received.
    /// </summary>
    protected override void BindData()
    {
        subscription?.Dispose();
        subscription = null;
        var newArea = ViewModel.Area?.ToString() ?? string.Empty;
        if (newArea != AreaToBeRendered)
            RootControl = null; // Only clear when the area actually changed
        AreaToBeRendered = newArea;
        base.BindData();
        DataBind(ViewModel.ProgressMessage, x => x.ProgressMessage);
        DataBind(ViewModel.ShowProgress, x => x.ShowProgress);
        DataBind(ViewModel.SpinnerType, x => x.SpinnerType);

        if (Stream is null)
            return;

        // When area is empty, GetControlStream returns a NamedAreaControl pointing to the default area.
        // Bounded, throttled, fully-reactive retry: a transiently unaddressable area (per-node hub
        // still bootstrapping, NodeType mid-compile, activity node not yet routable) self-heals within
        // a few backoff steps; an inexistent address gives up after AreaStreamRetry.DefaultMaxRetries
        // instead of resubscribing forever (the NotFound message storm that wedged the partition).
        // A CompilationInProgress NACK is NOT retried — it falls straight through to the error handler
        // below, which swaps to the Progress view at once.
        var controlStream = Stream.GetControlStream(AreaToBeRendered)
            .RetryAreaWithBackoff(AreaErrorClassifier.ShouldRetryArea);

        AddBinding(controlStream
            .Subscribe(
                x =>
                {
                    if (IsViewDisposed) return;
                    try
                    {
                        InvokeAsync(() =>
                        {
                            if (IsViewDisposed) return;
                            try
                            {
                                var control = x as UiControl;
                                // Keep-last-good: a TRANSIENT null control emission — the node/area
                                // stream momentarily reducing to empty mid-round (the "round-N vanish")
                                // — must NOT tear down a live child view. Ignore null once we already
                                // have a control: clearing RootControl flips `@if (RootControl != null)`
                                // to false, DISPOSING the child DispatchView and its component. For a
                                // data-bound child like the thread's ThreadChatControl that meant the
                                // whole ThreadChatView (and its Monaco composer) was destroyed and
                                // recreated the instant a round started executing, so the composer lost
                                // keyboard focus mid-stream (ChatComposerStreamingFocusTest). A genuine
                                // area change still clears RootControl in BindData, and real
                                // unavailability comes through the error/retry handler below — never as a
                                // null next-emission on the same area.
                                if (control is null && RootControl is not null)
                                    return;
                                if ((RootControl is null && control is null) || (RootControl != null && RootControl.Equals(control)))
                                    return;
                                RootControl = control;
                                if (RootControl is not null)
                                {
                                    DataBind(RootControl.PageTitle, y => y.PageTitle);
                                    DataBind(RootControl.Meta, y => y.MetaAttributes);
                                }
                                Logger.LogDebug("Setting area {Area} to rendering area {AreaToBeRendered} to type {Type}", Area,
                                    AreaToBeRendered, control?.GetType().Name ?? "null");
                                RequestStateChange();
                            }
                            catch (ObjectDisposedException) { /* renderer gone */ }
                        });
                    }
                    catch (ObjectDisposedException) { /* renderer gone */ }
                },
                error =>
                {
                    // ObjectDisposedException is a benign teardown artifact — the area stream's
                    // upstream hub or workspace was disposed during navigation/component swap.
                    // Don't surface it as a user-visible "Error loading area" markdown.
                    if (IsViewDisposed || error is ObjectDisposedException)
                    {
                        Logger.LogDebug(error, "Suppressed teardown error in control stream for area {Area}", AreaToBeRendered);
                        return;
                    }

                    // Routing-grain NACK: the target hub cannot activate because its NodeType
                    // is mid-compile. Swap RootControl to a LayoutAreaControl pointing at the
                    // NodeType hub's "Progress" area — Blazor renders that as a fresh
                    // LayoutAreaView whose stream binds to the NodeType's own MeshNode stream
                    // (status lines) and, transitively, the compile activity's progress.
                    // When the compile settles and the original area becomes addressable, the
                    // user re-navigates / re-mounts and the regular path resumes. Detected
                    // strictly on the typed Failure.ErrorType — no string sniff.
                    if (AreaErrorClassifier.TryGetCompilationInProgressNodeType(error) is { } nodeTypePath)
                    {
                        Logger.LogInformation(
                            "NACK CompilationInProgress for area {Area} on NodeType {NodeType} — swapping to Progress",
                            AreaToBeRendered, nodeTypePath);
                        try
                        {
                            InvokeAsync(() =>
                            {
                                if (IsViewDisposed) return;
                                try
                                {
                                    RootControl = new LayoutAreaControl(
                                        new Address(nodeTypePath),
                                        new LayoutAreaReference(NodeTypeLayoutAreas.ProgressArea));
                                    RequestStateChange();
                                }
                                catch (ObjectDisposedException) { /* renderer gone */ }
                            });
                        }
                        catch (ObjectDisposedException) { /* renderer gone */ }
                        return;
                    }

                    // The target hub's INITIALIZATION FAILED — a BuildupAction faulted or hung, so
                    // the hub opened its Initialize gate in a FAILED state and now answers every
                    // request with a typed DeliveryFailure carrying the real reason ("Hub '…'
                    // initialization failed: <reason>"; see HubInitializationFailure.md). This is
                    // TERMINAL-with-reason: a durably-failed hub will NOT recover on resubscribe, so
                    // it is neither retried (the bounded reactive retry above already skips it — see
                    // AreaErrorClassifier.ShouldRetryArea) nor collapsed into the generic "did not
                    // become addressable" spinner. Render the actual reason so operators see WHAT
                    // broke without reading server logs — and, because the reason stands on its own,
                    // it stays informative even when the gate never opened and AreaToBeRendered is
                    // empty (no area name ever resolved). Detected on the typed Failure — no NodeType
                    // path lookup, no retry. Issue #323.
                    if (AreaErrorClassifier.TryGetInitializationFailureReason(error) is { } initReason)
                    {
                        Logger.LogWarning(error,
                            "Area {Area} target hub initialization failed — rendering the init-error reason instead of the generic 'did not become addressable' banner. Reason={Reason}",
                            AreaToBeRendered, initReason);
                        try
                        {
                            InvokeAsync(() =>
                            {
                                if (IsViewDisposed) return;
                                try
                                {
                                    RootControl = new MarkdownControl(
                                        $"**This view could not be initialised.**\n\n{initReason}");
                                    RequestStateChange();
                                }
                                catch (ObjectDisposedException) { /* renderer gone */ }
                            });
                        }
                        catch (ObjectDisposedException) { /* renderer gone */ }
                        return;
                    }

                    // Transient hub/network failures (request timeouts, undeliverable
                    // routing, dropped circuit) are usually self-healing — the upstream
                    // hub finishes initialising, the security pipeline emits its first
                    // value, the per-node hub comes back online — and a subsequent
                    // navigation will resubscribe. Don't render the framework-internal
                    // "No response received in hub …" markdown to the user (hostile
                    // and unactionable). Log at Warning, leave the previous RootControl
                    // in place so the GUI doesn't flicker between "loading" and "error",
                    // and let the next BindData (route change, parameter change) restart
                    // the subscription naturally. Auto-retrying from this handler used
                    // to be tempting but caused a feedback loop: each retry recreated
                    // a subscription that emitted null first, the success handler reset
                    // the counter, the next failure re-armed retries, and the GUI looped
                    // forever consuming circuit bandwidth.
                    if (AreaErrorClassifier.IsTransientHubFailure(error))
                    {
                        // By the time a transient failure reaches here the bounded reactive
                        // retry (RetryAreaWithBackoff, applied to controlStream above) is
                        // ALREADY exhausted — the upstream had AreaStreamRetry.DefaultMaxRetries
                        // attempts over ~8s of backoff to come online and didn't. Give up and
                        // report, rather than spin forever (the inexistent-address storm) or
                        // silently keep a stale render that never updates. The earlier
                        // "keep previous, no retry" behaviour is now the retry's job.
                        Logger.LogWarning(error,
                            "Area {Area} unavailable after {Retries} reactive retries — reporting failure. Hub={Message}",
                            AreaToBeRendered, AreaStreamRetry.DefaultMaxRetries, error.Message);
                        try
                        {
                            InvokeAsync(() =>
                            {
                                if (IsViewDisposed) return;
                                try
                                {
                                    RootControl = new MarkdownControl(
                                        $"**Area unavailable.** The view at `{AreaToBeRendered}` did not become "
                                        + $"addressable after {AreaStreamRetry.DefaultMaxRetries} retries — it may still be "
                                        + "initialising or its NodeType may be compiling. Reload to retry.");
                                    RequestStateChange();
                                }
                                catch (ObjectDisposedException) { /* renderer gone */ }
                            });
                        }
                        catch (ObjectDisposedException) { /* renderer gone */ }
                        return;
                    }

                    // The target node/hub is GONE (routing NotFound). Its raw diagnostic
                    // ("No node found at '…/_Activity/markdown-…'. Closest ancestor is '…'
                    // (remainder='…')") is a framework-internal string that must NEVER reach an end
                    // user. It surfaced verbatim when an embedded per-view interactive-kernel Activity
                    // idle-disposed (KernelContainer.DisposeOnTimeout, 15 min) under a still-open area:
                    // there is no push-signal for owner death — only this control stream's own re-read
                    // discovers the miss, which is exactly why this handler is the right place to catch
                    // it. Render a graceful, honest placeholder instead of the raw error, still logging
                    // the failure for authors. NOT retried above (a gone node won't self-heal — that is
                    // the transient "not yet online" case, a different message).
                    if (AreaErrorClassifier.IsNodeGoneNotFound(error))
                    {
                        Logger.LogWarning(error,
                            "Area {Area} target is gone (routing NotFound) — rendering unavailable placeholder instead of the raw diagnostic",
                            AreaToBeRendered);
                        try
                        {
                            InvokeAsync(() =>
                            {
                                if (IsViewDisposed) return;
                                try
                                {
                                    RootControl = new MarkdownControl(
                                        "**This view is no longer available.** It may have been removed, or its "
                                        + "interactive session ended. Reload the page to retry.");
                                    RequestStateChange();
                                }
                                catch (ObjectDisposedException) { /* renderer gone */ }
                            });
                        }
                        catch (ObjectDisposedException) { /* renderer gone */ }
                        return;
                    }

                    // Access denied / validation failures are user-action outcomes (the user
                    // lacks the right; the input was invalid) — not engineering errors. Log them
                    // as Warning so production log dashboards don't page on every "user clicked
                    // a thing they couldn't do". Real errors (NullReferenceException,
                    // IO failures, runtime crashes) still land at Error level.
                    if (AreaErrorClassifier.IsExpectedUserActionFailure(error))
                        Logger.LogWarning(error, "Expected user-action failure in control stream for area {Area}: {Message}",
                            AreaToBeRendered, error.Message);
                    else
                        Logger.LogError(error, "Error in control stream for area {Area}", AreaToBeRendered);

                    // "No access ⇒ redirect": when the denied node's access policy configures a
                    // RedirectOnDenied page (PartitionAccessPolicy — e.g. a public course cover / sign-up),
                    // send the viewer THERE instead of a dead-end "Access denied" card. Guards: only the
                    // page's TOP-LEVEL area redirects (an embedded gated area must never hijack the whole
                    // page); the target comes from the node's OWN policy (reliable, not a guess); and
                    // IsSafeRedirect blocks redirecting the target itself (or a node under it), so it can
                    // never loop. The lookup is best-effort + bounded: on error/timeout it shows the honest
                    // error, never a swallow. When no RedirectOnDenied is set, it falls through to the error.
                    if (Top
                        && AreaErrorClassifier.TryGetAccessDeniedPath(error) is { } deniedPath)
                    {
                        void ShowError() => InvokeAsync(() =>
                        {
                            if (IsViewDisposed) return;
                            try
                            {
                                RootControl = new MarkdownControl($"**Error loading area:** {error.Message}");
                                RequestStateChange();
                            }
                            catch (ObjectDisposedException) { /* renderer gone */ }
                        });

                        Hub.GetRedirectOnDenied(deniedPath)
                            .Take(1)
                            .Timeout(TimeSpan.FromSeconds(5))
                            .Subscribe(
                                redirectPath =>
                                {
                                    if (IsViewDisposed) return;
                                    if (!AreaErrorClassifier.IsSafeRedirect(deniedPath, redirectPath))
                                    {
                                        ShowError();
                                        return;
                                    }
                                    InvokeAsync(() =>
                                    {
                                        if (IsViewDisposed) return;
                                        try
                                        {
                                            RootControl = new RedirectControl($"/{redirectPath!.TrimStart('/')}");
                                            RequestStateChange();
                                        }
                                        catch (ObjectDisposedException) { /* renderer gone */ }
                                    });
                                },
                                _ => { if (!IsViewDisposed) ShowError(); }); // lookup failed → honest error
                        return;
                    }

                    try
                    {
                        InvokeAsync(() =>
                        {
                            if (IsViewDisposed) return;
                            try
                            {
                                RootControl = new MarkdownControl($"**Error loading area:** {error.Message}");
                                RequestStateChange();
                            }
                            catch (ObjectDisposedException) { /* renderer gone */ }
                        });
                    }
                    catch (ObjectDisposedException) { /* renderer gone */ }
                },
                () =>
                {
                    Logger.LogDebug("Control stream completed for area {Area}", AreaToBeRendered);
                }
            )
        );
    }

}
