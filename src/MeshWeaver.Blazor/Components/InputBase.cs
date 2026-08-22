using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Base class for text-input form components that carry size and length constraints
/// in addition to the core form-field parameters provided by <c>FormComponentBase</c>.
/// </summary>
/// <typeparam name="TViewModel">The layout control model type, constrained to <c>IInputFormControl</c>.</typeparam>
/// <typeparam name="TView">The concrete Blazor view component deriving from this base.</typeparam>
/// <typeparam name="TData">The CLR type of the data value this input edits.</typeparam>
public abstract class InputBase<TViewModel, TView, TData> : FormComponentBase<TViewModel, TView, TData>
    where TViewModel : UiControl, IInputFormControl
    where TView : InputBase<TViewModel, TView, TData>
{

    /// <summary>Visible character width hint for the underlying HTML input element.</summary>
    protected int Size { get; set; }
    /// <summary>Minimum number of characters the user must enter; enforced by browser validation.</summary>
    protected int? MinLength { get; set; }
    /// <summary>Maximum number of characters the user may enter; enforced by browser validation.</summary>
    protected int? MaxLength { get; set; }


    /// <summary>
    /// Extends the base binding by additionally wiring <c>Size</c>, <c>MinLength</c>,
    /// and <c>MaxLength</c> from the view-model's <c>IInputFormControl</c> properties.
    /// </summary>
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
