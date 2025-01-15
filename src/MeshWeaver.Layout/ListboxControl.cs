using MeshWeaver.Data;

namespace MeshWeaver.Layout;

/// <summary>
/// Represents a listbox control with customizable properties.
/// </summary>
/// <param name="Data">The data associated with the listbox control.</param>
public record ListboxControl(object Data, object Options) : ListControlBase<ListboxControl>(Data, Options);

/// <summary>
/// Represents a list control interface with options.
/// </summary>
public interface IListControl : IFormComponent
{
    /// <summary>
    /// Gets or initializes the options for the list control.
    /// </summary>
    object Options { get; init; }
}

/// <summary>
/// Represents the base class for list controls with customizable properties.
/// </summary>
/// <typeparam name="TControl">The type of the list control.</typeparam>
/// <param name="Data">The data property associated with the list control, normally in form of .</param>
/// <param name="Options">The options to be chosen from <see cref="JsonPointerReference"/>.</param>
public abstract record ListControlBase<TControl>(object Data, object Options)
    : UiControl<TControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion), IListControl
    where TControl : ListControlBase<TControl>
{
    /// <summary>
    /// The label bound to this control
    /// </summary>
    public object Label { get; init; }


    /// <summary>
    /// Sets the label.
    /// </summary>
    /// <param name="label"></param>
    /// <returns></returns>
    public TControl WithLabel(object label)
        => This with { Label = label };

    /// <summary>
    /// Sets the options for the list control.
    /// </summary>
    /// <param name="options">The options to set.</param>
    /// <returns>A new instance of the list control with the specified options.</returns>
    public TControl WithOptions(object options) => (TControl)this with { Options = options };

    /// <summary>
    /// Sets the options for the list control from an enumerable collection.
    /// </summary>
    /// <typeparam name="T">The type of the options.</typeparam>
    /// <param name="options">The options to set.</param>
    /// <returns>A new instance of the list control with the specified options.</returns>
    public TControl WithOptions<T>(IEnumerable<T> options) => 
        WithOptions((object)options.Select(o => (Option)new Option<T>(o, o.ToString())).ToArray());

}

public abstract record Option(string Text)
{
    public abstract object GetItem();
}

public record Option<TItem>(TItem Item, string Text) : Option(Text)
{
    public override object GetItem() => Item;
}

public enum SelectPosition
{
    Above,
    Below
}
