using System.Linq.Expressions;
using Json.Pointer;

namespace OpenSmc.Layout;

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

public record SelectControl(object Data)
    : UiControl<SelectControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data)
{
    public IReadOnlyCollection<Option> Options { get; init; }
    public SelectControl WithOptions<T>(IReadOnlyCollection<Option<T>> options) => this with { Options = options };
    public SelectControl WithOptions<T>(IEnumerable<T> options) => WithOptions(options.Select(o => new Option<T>(o, o.ToString())).ToArray());

    public string OptionText { get; init; }
}

