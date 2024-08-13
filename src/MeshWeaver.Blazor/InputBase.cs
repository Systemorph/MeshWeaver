using MeshWeaver.Data;
using MeshWeaver.Layout;

namespace MeshWeaver.Blazor;

public abstract class InputBase<TViewModel, TView, TData> : BlazorView<TViewModel, TView>
    where TViewModel : UiControl, IInputControl
    where TView:InputBase<TViewModel, TView, TData>
{
    protected TData Data
    {
        get => InnerData;
        set {
            var needsUpdate = !EqualityComparer<TData>.Default.Equals(InnerData, value);
            InnerData = value;
            if (needsUpdate)
                UpdatePointer(InnerData, DataPointer);
        }
    }

    private TData InnerData { get; set; }

    protected string Placeholder { get; set; }
    protected bool Disabled { get; set; }
    protected bool AutoFocus { get; set; }
    protected bool Immediate { get; set; }
    protected int ImmediateDelay { get; set; }

    private JsonPointerReference DataPointer { get; set; }


    protected override void BindData()
    {
        base.BindData();
        if (ViewModel != null)
        {
            DataBind<TData>(ViewModel.Data, x => x.InnerData);
            DataBind<string>(ViewModel.Placeholder, x => x.Placeholder);
            DataBind<bool>(ViewModel.Disabled, x => x.Disabled);
            DataBind<bool>(ViewModel.AutoFocus, x => x.AutoFocus);
            DataBind<bool>(ViewModel.Immediate, x => x.Immediate);
            DataBind<int>(ViewModel.ImmediateDelay, x => x.ImmediateDelay);

            DataPointer = ViewModel.Data as JsonPointerReference;
        }
    }
}
