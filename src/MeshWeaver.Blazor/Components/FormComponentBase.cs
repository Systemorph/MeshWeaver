using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.DataBinding;

namespace MeshWeaver.Blazor.Components;

public abstract class FormComponentBase<TViewModel, TView, TValue> : BlazorView<TViewModel, TView>
    where TViewModel : UiControl, IFormControl
    where TView : FormComponentBase<TViewModel, TView, TValue>
{
    private TValue value;

    public const string Edit = nameof(Edit);
    protected string Label { get; set; }

    private Subject<TValue> valueUpdateSubject;
    protected TValue Value
    {
        get => value;
        set
        {
            valueUpdateSubject.OnNext(value);
            this.value = value;
        }
    }

    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.Label, x => x.Label);
        valueUpdateSubject = new();
        AddBinding(valueUpdateSubject
            .Debounce(TimeSpan.FromMilliseconds(20))
            .DistinctUntilChanged()
            .Skip(1)
            .Subscribe(x => UpdatePointer(ConvertToData(value), Pointer))
        );
        DataBind(ViewModel.Data, x => x.Value, ConversionToValue);
        Pointer = ViewModel.Data as JsonPointerReference;
    }

    protected virtual Func<object, TValue> ConversionToValue => null;

    protected virtual object ConvertToData(TValue v) => v;

    protected virtual bool NeedsUpdate(TValue v)
    {
        return !Equals(value, v);
    }

    protected JsonPointerReference Pointer { get; set; }

}
