
using MeshWeaver.Domain.Layout;

namespace MeshWeaver.Layout;

public record ListboxControl(object Data) : ListControlBase<ListboxControl>(Data), IListControl;

public interface IListControl : IFormComponent
{
    IReadOnlyCollection<Option> Options { get; init; }
}

public abstract record ListControlBase<TControl>(object Data)
    : UiControl<TControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    where TControl : ListControlBase<TControl>, IListControl
{
    public IReadOnlyCollection<Option> Options { get; init; }

    public TControl WithOptions(IReadOnlyCollection<Option> options) => (TControl) this with { Options = options };

    public TControl WithOptions<T>(IEnumerable<T> options) => 
        WithOptions(options.Select(o => (Option)new Option<T>(o, o.ToString())).ToArray());

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
