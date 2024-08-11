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
            DataBindProperty<TData>(ViewModel.Data, x => x.InnerData);
            DataBindProperty<string>(ViewModel.Placeholder, x => x.Placeholder);
            DataBindProperty<bool>(ViewModel.Disabled, x => x.Disabled);
            DataBindProperty<bool>(ViewModel.AutoFocus, x => x.AutoFocus);
            DataBindProperty<bool>(ViewModel.Immediate, x => x.Immediate);
            DataBindProperty<int>(ViewModel.ImmediateDelay, x => x.ImmediateDelay);

            DataPointer = ViewModel.Data as JsonPointerReference;
        }
    }
}
