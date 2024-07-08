using OpenSmc.Data;
using OpenSmc.Layout;

namespace OpenSmc.Blazor;

public abstract class InputBase<TViewModel, TData> : BlazorView<TViewModel>
    where TViewModel : UiControl, IInputControl
{
    protected TData Data
    {
        get => data;
        set {
            var needsUpdate = !EqualityComparer<TData>.Default.Equals(data, value);
            data = value;
            if (needsUpdate)
                UpdatePointer(data, DataPointer);
        }
    }

    private TData data;

    protected string Placeholder { get; set; }
    protected bool Disabled { get; set; }
    protected bool AutoFocus { get; set; }
    protected bool Immediate { get; set; }
    protected int ImmediateDelay { get; set; }

    private JsonPointerReference DataPointer { get; set; }


    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        if (ViewModel != null)
        {
            DataBind<TData>(ViewModel.Data, x => data = x);
            DataBind<string>(ViewModel.Placeholder, x => Placeholder = x);
            DataBind<bool>(ViewModel.Disabled, x => Disabled = x);
            DataBind<bool>(ViewModel.AutoFocus, x => AutoFocus = x);
            DataBind<bool>(ViewModel.Immediate, x => Immediate = x);
            DataBind<int>(ViewModel.ImmediateDelay, x => ImmediateDelay = x);

            DataPointer = ViewModel.Data as JsonPointerReference;
        }
    }
}
