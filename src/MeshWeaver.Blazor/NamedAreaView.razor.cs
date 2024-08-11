using System.Reactive.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeshWeaver.Layout;
using MeshWeaver.Utils;

namespace MeshWeaver.Blazor;

public partial class NamedAreaView
{
    private IDisposable subscription;
    [Inject]
    private ILogger<LayoutArea> Logger { get; set; }
    private UiControl RootControl { get; set; }



    private string DisplayArea { get; set; }
    private bool ShowProgress { get; set; }
    private string ViewModelArea { get; set; }

    private object RenderedStream { get; set; }
    protected override void BindData()
    {
        base.BindData();
        DataBind<string>(ViewModel.Area, x =>
        {
            if(RenderedStream == Stream && x == ViewModelArea)
                return false;
            ViewModelArea = x;
            subscription?.Dispose();
            subscription = null;
            if (ViewModelArea == null)
                return true;
            subscription = Stream.GetControlStream(ViewModelArea)
                .Subscribe(item => InvokeAsync(() => Render(item as UiControl)));
            return true;
        });
        DataBindProperty<string>(ViewModel.DisplayArea, x => x.DisplayArea);
        DataBindProperty<bool>(ViewModel.ShowProgress, x => x.ShowProgress);

        DisplayArea ??= ViewModelArea.Wordify();
    }


    private void Render(UiControl control)
    {
        Logger.LogDebug(
            "Changing area {Area} to {Instance}",
            ViewModelArea,
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
