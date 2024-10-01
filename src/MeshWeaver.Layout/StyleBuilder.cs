using System.Collections.Immutable;
using System.Dynamic;

namespace MeshWeaver.Layout;

/// <summary>
/// Provides methods to build and configure styles dynamically.
/// </summary>
public record StyleBuilder
{
    private ImmutableList<Action<dynamic>> actions = ImmutableList.Create<Action<dynamic>>();

    /// <summary>
    /// Sets the display style to "flex".
    /// </summary>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder Flex() => WithDisplay("flex");

    /// <summary>
    /// Sets the display style.
    /// </summary>
    /// <param name="value">The display style value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithDisplay(object value) => With(d => d.Display = value);

    /// <summary>
    /// Sets the width style.
    /// </summary>
    /// <param name="value">The width value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithWidth(object value) => With(d => d.Width = value);

    /// <summary>
    /// Sets the minimum width style.
    /// </summary>
    /// <param name="value">The minimum width value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithMinWidth(object value) => With(d => d.MinWidth = value);

    /// <summary>
    /// Sets the height style.
    /// </summary>
    /// <param name="value">The height value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithHeight(object value) => With(d => d.Height = value);

    /// <summary>
    /// Sets the flex direction style.
    /// </summary>
    /// <param name="value">The flex direction value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithFlexDirection(object value) => With(d => d.FlexDirection = value);

    /// <summary>
    /// Sets the flex wrap style.
    /// </summary>
    /// <param name="value">The flex wrap value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithFlexWrap(object value) => With(d => d.FlexWrap = value);

    /// <summary>
    /// Sets the flex flow style.
    /// </summary>
    /// <param name="value">The flex flow value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithFlexFlow(object value) => With(d => d.FlexFlow = value);

    /// <summary>
    /// Sets the justify content style.
    /// </summary>
    /// <param name="value">The justify content value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithJustifyContent(object value) => With(d => d.JustifyContent = value);

    /// <summary>
    /// Sets the align content style.
    /// </summary>
    /// <param name="value">The align content value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithAlignContent(object value) => With(d => d.AlignContent = value);

    /// <summary>
    /// Sets the align items style.
    /// </summary>
    /// <param name="value">The align items value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithAlignItems(object value) => With(d => d.AlignItems = value);

    /// <summary>
    /// Sets the gap style.
    /// </summary>
    /// <param name="value">The gap value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithGap(object value) => With(d => d.Gap = value);

    /// <summary>
    /// Sets the row gap style.
    /// </summary>
    /// <param name="value">The row gap value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithRowGap(object value) => With(d => d.RowGap = value);

    /// <summary>
    /// Sets the column gap style.
    /// </summary>
    /// <param name="value">The column gap value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithColumnGap(object value) => With(d => d.ColumnGap = value);

    /// <summary>
    /// Sets the align self style.
    /// </summary>
    /// <param name="value">The align self value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
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
