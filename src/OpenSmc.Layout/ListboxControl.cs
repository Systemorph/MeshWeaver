namespace OpenSmc.Layout;

public record ListboxControl(object Data) : ListControlBase<ListboxControl>(Data), IListControl;

public interface IListControl : IUiControl
{
    IReadOnlyCollection<Option> Options { get; init; }
}

public abstract record ListControlBase<TControl>(object Data)
    : UiControl<TControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data)
    where TControl : ListControlBase<TControl>, IListControl
{
    public IReadOnlyCollection<Option> Options { get; init; }

    public TControl WithOptions<T>(IReadOnlyCollection<Option<T>> options) => (TControl) this with { Options = options };

    public TControl WithOptions<T>(IEnumerable<T> options) => 
        WithOptions(options.Select(o => new Option<T>(o, o.ToString())).ToArray());
}

public abstract record Option(string Text)
{
    public abstract object GetItem();
    public abstract Type GetItemType();
}

public record Option<T>(T Item, string Text) : Option(Text)
{
    public override object GetItem() => Item;
    public override Type GetItemType() => typeof(T);
}

public enum SelectPosition
{
    Above,
    Below
}
