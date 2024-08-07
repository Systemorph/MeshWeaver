using System.Collections.Immutable;
using System.Dynamic;

namespace MeshWeaver.Layout;

public record StyleBuilder
{
    private ImmutableList<Action<dynamic>> actions = ImmutableList.Create<Action<dynamic>>();

    public StyleBuilder Flex() => WithDisplay("flex");
    public StyleBuilder WithDisplay(object value) => With(d => d.Display = value);
    public StyleBuilder WithWidth(object value) => With(d => d.Width = value);
    public StyleBuilder WithMinWidth(object value) => With(d => d.MinWidth = value);
    public StyleBuilder WithHeight(object value) => With(d => d.Height = value);
    public StyleBuilder WithFlexDirection(object value) => With(d => d.FlexDirection = value);
    public StyleBuilder WithFlexWrap(object value) => With(d => d.FlexWrap = value);
    public StyleBuilder WithFlexFlow(object value) => With(d => d.FlexFlow = value);
    public StyleBuilder WithJustifyContent(object value) => With(d => d.JustifyContent = value);
    public StyleBuilder WithAlignContent(object value) => With(d => d.AlignContent = value);
    public StyleBuilder WithAlignItems(object value) => With(d => d.AlignItems = value);
    public StyleBuilder WithGap(object value) => With(d => d.Gap = value);
    public StyleBuilder WithRowGap(object value) => With(d => d.RowGap = value);
    public StyleBuilder WithColumnGap(object value) => With(d => d.ColumnGap = value);
    public StyleBuilder WithAlignSelf(object value) => With(d => d.AlignSelf = value);
    public StyleBuilder WithFlexBasis(object value) => With(d => d.FlexBasis = value);
    public StyleBuilder WithFlexGrow(object value) => With(d => d.FlexGrow = value);
    public StyleBuilder WithFlexShrink(object value) => With(d => d.FlexShrink = value);
    public StyleBuilder WithOrder(object value) => With(d => d.Order = value);
    public StyleBuilder WithMargin(object value) => With(d => d.Margin = value);
    public StyleBuilder WithPadding(object value) => With(d => d.Padding = value);


    private StyleBuilder With(Action<dynamic> action)
    {
        return this with { actions = actions.Add(action) };
    }

    public object Build()
    {
        dynamic style = new ExpandoObject();
        foreach (var action in actions)
            action(style);
        return style;
    }
}
