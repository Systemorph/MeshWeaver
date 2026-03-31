using MeshWeaver.Layout;
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
    private CancellationTokenSource? _timeoutCts;

    protected override void BindData()
    {
        subscription?.Dispose();
        subscription = null;
        _timeoutCts?.Cancel();
        _timeoutCts?.Dispose();
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

        // Start a timeout — if no content arrives within 15s, show diagnostic info
        if (ShowProgress && !Top)
        {
            _timeoutCts = new CancellationTokenSource();
            _ = ShowTimeoutMessageAsync(_timeoutCts.Token);
        }

        AddBinding(controlStream
            .Subscribe(
                x =>
                {
                    _timeoutCts?.Cancel(); // Content arrived, cancel timeout
                    InvokeAsync(() =>
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
                    });
                },
                error =>
                {
                    _timeoutCts?.Cancel();
                    Logger.LogError(error, "Error in control stream for area {Area}", AreaToBeRendered);
                    InvokeAsync(() =>
                    {
                        RootControl = new MarkdownControl($"**Error loading area:** {error.Message}");
                        RequestStateChange();
                    });
                },
                () =>
                {
                    Logger.LogDebug("Control stream completed for area {Area}", AreaToBeRendered);
                }
            )
        );
    }

    private async Task ShowTimeoutMessageAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(15_000, ct);
            // Still no content after 15s — show diagnostic
            await InvokeAsync(() =>
            {
                if (RootControl == null)
                {
                    var owner = Stream?.Owner?.ToString() ?? "(unknown)";
                    var area = AreaToBeRendered ?? "(default)";
                    Logger.LogWarning("Layout area timeout: no content after 15s for {Owner}/{Area}", owner, area);
                    RootControl = new MarkdownControl(
                        $"**Timed out** waiting for `{owner}` area `{area}`");
                    ShowProgress = false;
                    RequestStateChange();
                }
            });
        }
        catch (TaskCanceledException) { /* Content arrived or component disposed */ }
    }


}
