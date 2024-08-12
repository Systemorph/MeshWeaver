using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeshWeaver.Layout;

namespace MeshWeaver.Blazor;

public partial class NamedAreaView
{
    private IDisposable subscription;
    [Inject]
    private ILogger<LayoutArea> Logger { get; set; }
    private UiControl RootControl { get; set; }



    private string DisplayArea { get; set; }
    private bool ShowProgress { get; set; }

    protected override void BindData()
    {
        base.BindData();
        DataBindProperty(ViewModel.DisplayArea, x => x.DisplayArea);
        DataBindProperty(ViewModel.ShowProgress, x => x.ShowProgress);
        BindToStream();
    }

    protected void BindToStream() =>
        DataBind<string>(ViewModel.Area, x =>
        {
            Area = x;
            subscription?.Dispose();
            subscription = null;
            if (Area == null)
                return true;
            subscription = Stream.GetControlStream(Area)
                .Subscribe(item => InvokeAsync(() => Render(item as UiControl)));
            return true;
        });



    private void Render(UiControl control)
    {
        Logger.LogDebug(
            "Changing area {Area} to {Instance}",
            Area,
            control?.GetType().Name
        );
        if (Equals(RootControl, control))
            return;
        RootControl = control;
        StateHasChanged();
    }

    public override void Dispose()
    {
        subscription?.Dispose();
        subscription = null;
        base.Dispose();
    }
}
