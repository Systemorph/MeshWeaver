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

    private IDisposable? subscription;
    private string? PageTitle;
    private IDictionary<string, object>? MetaAttributes;

    private string? AreaToBeRendered { get; set; }
    protected override void BindData()
    {
        subscription?.Dispose();
        subscription = null;
        RootControl = null;
        AreaToBeRendered = ViewModel.Area.ToString();
        base.BindData();
        DataBind(ViewModel.ProgressMessage, x => x.ProgressMessage);
        DataBind(ViewModel.ShowProgress, x => x.ShowProgress);
        if (AreaToBeRendered is not null)
            AddBinding(Stream.GetControlStream(AreaToBeRendered!)
                .Subscribe(x =>
                {
                    InvokeAsync(() =>
                    {
                        if (RootControl is null && x is null || RootControl != null && RootControl.Equals(x))
                            return;
                        RootControl = x;
                        if (RootControl is not null)
                        {
                            DataBind(RootControl.PageTitle, y => y.PageTitle);
                            DataBind(RootControl.Meta, y => y.MetaAttributes);
                        }
                        Logger.LogDebug("Setting area {Area} to rendering area {AreaToBeRendered} to type {Type}", Area,
                            AreaToBeRendered, x?.GetType().Name ?? "null");
                        RequestStateChange();
                    });
                })
            );
    }


}
