using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Components;

public partial class NamedAreaView
{
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
    /// Number of consecutive transient failures (timeout / "no response in hub")
    /// the area has absorbed without a successful emission. Capped at
    /// <see cref="MaxTransientRetries"/> to prevent runaway rebinds when the
    /// upstream hub is genuinely down. Reset to zero on every successful
    /// control emission.
    /// </summary>
    private int _transientRetryCount;
    private const int MaxTransientRetries = 3;
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromMilliseconds(500);

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

        // When area is empty, GetControlStream returns a NamedAreaControl pointing to the default area
        var controlStream = Stream.GetControlStream(AreaToBeRendered);

        AddBinding(controlStream
            .Subscribe(
                x =>
                {
                    if (IsViewDisposed) return;
                    // Successful emission resets the transient-retry budget so a
                    // brief upstream blip followed by recovery doesn't permanently
                    // exhaust the retry counter for the lifetime of this view.
                    _transientRetryCount = 0;
                    try
                    {
                        InvokeAsync(() =>
                        {
                            if (IsViewDisposed) return;
                            try
                            {
                                var control = x as UiControl;
                                if (RootControl is null && control is null || RootControl != null && RootControl.Equals(control))
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

                    // Transient hub/network failures (request timeouts, undeliverable
                    // routing, dropped circuit) are usually self-healing — the upstream
                    // hub finishes initialising, the security pipeline emits its first
                    // value, the per-node hub comes back online — and a fresh
                    // SubscribeRequest succeeds. Surface them to the user as an
                    // "Error loading area: No response received in hub …" markdown
                    // dump is hostile and unrecoverable from the GUI. Suppress the
                    // markdown, show a brief reconnecting placeholder, and re-bind
                    // the stream after a short delay. Cap the retry budget so a
                    // permanently-down hub still surfaces eventually.
                    if (IsTransientHubFailure(error))
                    {
                        var attempt = ++_transientRetryCount;
                        if (attempt <= MaxTransientRetries)
                        {
                            Logger.LogWarning(error,
                                "Transient hub failure on area {Area} (attempt {Attempt}/{Max}) — auto-reconnecting after {Delay}",
                                AreaToBeRendered, attempt, MaxTransientRetries, TransientRetryDelay);
                            try
                            {
                                InvokeAsync(async () =>
                                {
                                    if (IsViewDisposed) return;
                                    try
                                    {
                                        // Show a non-error placeholder while we re-subscribe.
                                        RootControl = new MarkdownControl(
                                            $"_Reconnecting (attempt {attempt}/{MaxTransientRetries})…_");
                                        RequestStateChange();
                                        await Task.Delay(TransientRetryDelay);
                                        if (IsViewDisposed) return;
                                        // Rebuild the subscription against the same area —
                                        // BindData disposes the old subscription and starts
                                        // fresh, so a recovered upstream is picked up.
                                        BindData();
                                    }
                                    catch (ObjectDisposedException) { /* renderer gone */ }
                                });
                            }
                            catch (ObjectDisposedException) { /* renderer gone */ }
                            return;
                        }
                        Logger.LogError(error,
                            "Transient hub failure on area {Area} exceeded retry budget ({Max}) — surfacing to user",
                            AreaToBeRendered, MaxTransientRetries);
                        // Fall through to the user-visible markdown below.
                    }

                    // Access denied / validation failures / not-found are user-action
                    // outcomes (the user lacks the right; the input was invalid; the
                    // node was deleted) — not engineering errors. Log them as Warning
                    // so production log dashboards don't page on every "user clicked
                    // a thing they couldn't do". Real errors (NullReferenceException,
                    // IO failures, runtime crashes) still land at Error level.
                    if (IsExpectedUserActionFailure(error))
                        Logger.LogWarning(error, "Expected user-action failure in control stream for area {Area}: {Message}",
                            AreaToBeRendered, error.Message);
                    else
                        Logger.LogError(error, "Error in control stream for area {Area}", AreaToBeRendered);
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

    /// <summary>
    /// Classifies an exception as an expected user-action outcome (access
    /// violation, validation rejection, not-found, etc.) versus an engineering
    /// error (null deref, IO crash, etc.). Used to choose the log level so the
    /// production log dashboard doesn't page on every "user clicked something
    /// they couldn't do".
    ///
    /// <para>Looks through the exception chain — <see cref="DeliveryFailureException"/>
    /// wraps the routing-layer failure message; <see cref="UnauthorizedAccessException"/>
    /// is the .NET-standard access-denied. Message-based matching for the
    /// rest because <c>DeliveryFailure.ErrorType</c> is internal to the
    /// messaging assembly and we don't expose it.</para>
    /// </summary>
    /// <summary>
    /// Classifies an exception as a transient hub/network failure that should
    /// trigger an automatic re-subscription rather than surface as a hostile
    /// "Error loading area" markdown to the user.
    ///
    /// <para>The dominant pattern is the framework's own
    /// <see cref="TimeoutException"/> from <c>MessageHub.BuildTimeoutMessage</c>
    /// — "No response received in hub … within … for request …" — which fires
    /// when the SubscribeRequest's target hub doesn't respond inside
    /// <see cref="MessageHubConfiguration.RequestTimeout"/> (default 30 s).
    /// Causes range from "the per-node hub is still bootstrapping its
    /// SecurityService data sources" to "the workspace synced query has not
    /// emitted yet" — both self-heal once the upstream catches up.
    /// <see cref="DeliveryFailureException"/> with the matching wording, and
    /// raw <c>OperationCanceledException</c> from a torn-down circuit, are
    /// also transient.</para>
    /// </summary>
    private static bool IsTransientHubFailure(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is TimeoutException) return true;
            if (e is OperationCanceledException) return true;
            var msg = e.Message ?? string.Empty;
            // The framework's own undeliverable / timeout banners — they reach
            // the GUI as DeliveryFailureException-wrapped messages, so the
            // typed check above doesn't always catch them.
            if (msg.Contains("No response received in hub", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("target hub was not found", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("undeliverable", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsExpectedUserActionFailure(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is UnauthorizedAccessException) return true;
            if (e is DeliveryFailureException)
            {
                var msg = e.Message ?? string.Empty;
                if (msg.Contains("Access denied", StringComparison.OrdinalIgnoreCase)) return true;
                if (msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)) return true;
                if (msg.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)) return true;
                if (msg.StartsWith("No node found", StringComparison.OrdinalIgnoreCase)) return true;
                if (msg.Contains("Validation failed", StringComparison.OrdinalIgnoreCase)) return true;
                if (msg.Contains("Validation error", StringComparison.OrdinalIgnoreCase)) return true;
                if (msg.Contains("not allowed", StringComparison.OrdinalIgnoreCase)) return true;
                if (msg.Contains("permission", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
}
