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
