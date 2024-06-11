using System.Collections;
using System.Linq.Expressions;
using Json.Pointer;

namespace OpenSmc.Layout.Views;

public record SelectControl()
    : UiControl<SelectControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    public IEnumerable Elements { get; init; }
    public string OptionText { get; init; }
}

public static class SelectControlBuilder
{
    public static SelectControl ToSelect<TItem>(this IEnumerable<TItem> elements,
        Func<SelectControlBuilder<TItem>, SelectControlBuilder<TItem>> configuration) =>
            configuration.Invoke(new() { Elements = elements }).Build();
}

public record SelectControlBuilder<TItem>
{
    public IEnumerable<TItem> Elements { get; init; }

    public object Data { get; init; }

    public string Label { get; init; }

    public string OptionValue { get; init; }

    public string OptionText { get; init; }

    public SelectControlBuilder<TItem> WithData(object data) =>
        this with { Data = data };

    public SelectControlBuilder<TItem> WithLabel(string label) =>
        this with { Label = label };

    public SelectControlBuilder<TItem> WithOptionValue(Expression<Func<TItem, object>> optionValue) =>
        this with { OptionValue = ParseExpression(optionValue) };

    public SelectControlBuilder<TItem> WithOptionText(Expression<Func<TItem, object>> optionText) =>
        this with { OptionText = ParseExpression(optionText) };

    private static string ParseExpression(Expression<Func<TItem, object>> expression) =>
        JsonPointer.Create(expression, 
                new PointerCreationOptions() {PropertyNameResolver = PropertyNameResolvers.CamelCase})
            .ToString();

    public SelectControl Build() => new()
    {
        Elements = Elements,
        Label = Label,
        OptionText = OptionText
    };
}
