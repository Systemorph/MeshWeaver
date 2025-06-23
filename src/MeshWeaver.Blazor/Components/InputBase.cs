using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

namespace MeshWeaver.Blazor.Components;

public abstract class InputBase<TViewModel, TView, TData> : FormComponentBase<TViewModel, TView, TData>
    where TViewModel : UiControl, IInputFormControl
    where TView : InputBase<TViewModel, TView, TData>
{

    protected int Size { get; set; }
    protected int? MinLength { get; set; }
    protected int? MaxLength { get; set; }


    protected override void BindData()
    {
        base.BindData();
        if (ViewModel != null)
        {
            DataBind(ViewModel.Size, x => x.Size);
            DataBind(ViewModel.MinLength, x => x.MinLength);
            DataBind(ViewModel.MaxLength, x => x.MaxLength);
        }
    }
}
