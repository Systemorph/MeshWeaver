using MeshWeaver.Data;
using MeshWeaver.Layout;

namespace MeshWeaver.Blazor;

public abstract class FormComponentBase<TViewModel, TView, TValue> : BlazorView<TViewModel, TView>
    where TViewModel : UiControl, IFormComponent
    where TView : FormComponentBase<TViewModel, TView, TValue>
{
    private TValue value;

    public const string Edit = nameof(Edit);

    protected TValue Value
    {
        get => value;
        set
        {
            var needsUpdate = NeedsUpdate(value);
            this.value = value;
            if (needsUpdate)
                UpdatePointer(ConvertToData(value), Pointer);
        }
    }

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Data, x => x.Value, ConversionToValue);
        Pointer = ViewModel.Data as JsonPointerReference;

    }

    protected virtual Func<object, TValue> ConversionToValue => null;

    protected virtual object ConvertToData(TValue v)
    {
        return v;
    }

    protected virtual  bool NeedsUpdate(TValue v)
    {
        return !Equals(this.value, v);
    }

    protected JsonPointerReference Pointer { get; set; }

}
