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


}
