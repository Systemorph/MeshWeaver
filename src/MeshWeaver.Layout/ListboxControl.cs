using MeshWeaver.Domain.Layout;

namespace MeshWeaver.Layout;

/// <summary>
/// Represents a listbox control with customizable properties.
/// </summary>
/// <param name="Data">The data associated with the listbox control.</param>
public record ListboxControl(object Data) : ListControlBase<ListboxControl>(Data), IListControl;

/// <summary>
/// Represents a list control interface with options.
/// </summary>
public interface IListControl : IFormComponent
{
    /// <summary>
    /// Gets or initializes the options for the list control.
    /// </summary>
    IReadOnlyCollection<Option> Options { get; init; }
}

/// <summary>
/// Represents the base class for list controls with customizable properties.
/// </summary>
/// <typeparam name="TControl">The type of the list control.</typeparam>
/// <param name="Data">The data associated with the list control.</param>
public abstract record ListControlBase<TControl>(object Data)
    : UiControl<TControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    where TControl : ListControlBase<TControl>, IListControl
{
    /// <summary>
    /// Gets or initializes the options for the list control.
    /// </summary>
    public IReadOnlyCollection<Option> Options { get; init; }

    /// <summary>
    /// Sets the options for the list control.
    /// </summary>
    /// <param name="options">The options to set.</param>
    /// <returns>A new instance of the list control with the specified options.</returns>
    public TControl WithOptions(IReadOnlyCollection<Option> options) => (TControl)this with { Options = options };

    /// <summary>
    /// Sets the options for the list control from an enumerable collection.
    /// </summary>
    /// <typeparam name="T">The type of the options.</typeparam>
    /// <param name="options">The options to set.</param>
    /// <returns>A new instance of the list control with the specified options.</returns>
    public TControl WithOptions<T>(IEnumerable<T> options) => 
        WithOptions(options.Select(o => (Option)new Option<T>(o, o.ToString())).ToArray());

    /// <summary>
    /// Determines whether the specified list control is equal to the current list control.
    /// </summary>
    /// <param name="other">The list control to compare with the current list control.</param>
    /// <returns><c>true</c> if the specified list control is equal to the current list control; otherwise, <c>false</c>.</returns>
    public virtual bool Equals(ListControlBase<TControl> other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return base.Equals(other) 
               && Options.SequenceEqual(other.Options) 
               && LayoutHelperExtensions.DataEquality(Data, other.Data);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            base.GetHashCode(), 
            Options.Aggregate(17, (x, y) => x ^y.GetHashCode()),
            LayoutHelperExtensions.DataHashCode(Data));
    }
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
