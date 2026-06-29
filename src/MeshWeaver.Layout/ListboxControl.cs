using MeshWeaver.Data;

namespace MeshWeaver.Layout;

/// <summary>
/// Represents a listbox control with customizable properties.
/// </summary>
/// <param name="Data">The data associated with the listbox control.</param>
/// <param name="Options">The set of selectable options rendered in the listbox.</param>
public record ListboxControl(object Data, object Options) : ListControlBase<ListboxControl>(Data, Options);

/// <summary>
/// Represents a list control interface with options.
/// </summary>
public interface IListControl : IFormControl
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
    : FormControlBase<TControl>(Data), IListControl
    where TControl : ListControlBase<TControl>
{
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
        WithOptions((object)options.Select(o => (Option)new Option<T>(o, o?.ToString() ?? "")).ToArray());

}

/// <summary>
/// Abstract base for a selectable option in a list control, carrying display text and an optional icon.
/// </summary>
/// <param name="Text">The display text shown to the user for this option.</param>
public abstract record Option(string Text)
{
    /// <summary>
    /// Optional icon identifier (Fluent icon name, emoji, or image URL) rendered alongside the option text.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>Returns the underlying item value for this option.</summary>
    /// <returns>The item object this option wraps.</returns>
    public abstract object GetItem();
    /// <summary>Returns the CLR type of the underlying item.</summary>
    /// <returns>The item's runtime type.</returns>
    public abstract Type GetItemType();
}

/// <summary>
/// A strongly-typed selectable option that pairs a value of type <typeparamref name="TItem"/> with display text.
/// </summary>
/// <typeparam name="TItem">The type of the item this option represents.</typeparam>
/// <param name="Item">The underlying item value.</param>
/// <param name="Text">The display text shown to the user.</param>
public record Option<TItem>(TItem Item, string Text) : Option(Text)
{
    /// <inheritdoc/>
    public override object GetItem() => Item!;
    /// <inheritdoc/>
    public override Type GetItemType()
        => typeof(TItem);
}

/// <summary>
/// Controls where a dropdown or popup selector is positioned relative to its trigger element.
/// </summary>
public enum SelectPosition
{
    /// <summary>The selector opens above the trigger element.</summary>
    Above,
    /// <summary>The selector opens below the trigger element.</summary>
    Below
}
