using System.Linq.Expressions;

namespace OpenSmc.Layout.Views;

public record SelectControl()
    : UiControl<SelectControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, null)
{
    public string OptionValue { get; init; }

    public string OptionText { get; init; }
}

public static class SelectControlBuilder
{
    public static SelectControl ToSelect<TItem>(this IReadOnlyCollection<TItem> elements,
        Func<SelectControlBuilder<TItem>, SelectControlBuilder<TItem>> configuration) =>
            configuration.Invoke(new() { Data = elements }).Build();
}

public record SelectControlBuilder<TItem>
{
    public IReadOnlyCollection<TItem> Data { get; init; }

    public string Label { get; init; }

    public string OptionValue { get; init; }

    public string OptionText { get; init; }

    public SelectControlBuilder<TItem> WithLabel(string label) =>
        this with { Label = label };

    public SelectControlBuilder<TItem> WithOptionValue(Expression<Func<TItem, string>> optionValue) =>
        this with { OptionValue = ParseExpression(optionValue) };

    public SelectControlBuilder<TItem> WithOptionText(Expression<Func<TItem, string>> optionText) =>
        this with { OptionText = ParseExpression(optionText) };

    private static string ParseExpression(Expression<Func<TItem, string>> expression) => 
        ((MemberExpression)expression.Body).Member.Name;

    public SelectControl Build() => new()
    {
        Data = Data,
        Label = Label,
        OptionValue = OptionValue,
        OptionText = OptionText
    };
}
