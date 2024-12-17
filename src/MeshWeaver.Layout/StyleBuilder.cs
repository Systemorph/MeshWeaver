using System.Text;

namespace MeshWeaver.Layout;

/// <summary>
/// Provides methods to build and configure styles dynamically.
/// </summary>
public record StyleBuilder
{
    public object Display { get; init; }
    public object Width { get; init; }
    public object MinWidth { get; init; }
    public object Height { get; init; }
    public object FlexDirection { get; init; }
    public object FlexWrap { get; init; }
    public object FlexFlow { get; init; }
    public object JustifyContent { get; init; }
    public object AlignContent { get; init; }
    public object AlignItems { get; init; }
    public object Gap { get; init; }
    public object RowGap { get; init; }
    public object ColumnGap { get; init; }
    public object AlignSelf { get; init; }
    public object FlexBasis { get; init; }
    public object FlexGrow { get; init; }
    public object FlexShrink { get; init; }
    public object Order { get; init; }
    public object Margin { get; init; }
    public object Padding { get; init; }
    public object Border { get; set; }


    /// <summary>
    /// Sets the display style to "flex".
    /// </summary>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder Flex() => this with { Display = "flex" };

    /// <summary>
    /// Sets the display style.
    /// </summary>
    /// <param name="value">The display style value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithDisplay(object value) => this with { Display = value };

    /// <summary>
    /// Sets the width style.
    /// </summary>
    /// <param name="value">The width value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithWidth(object value) => this with { Width = value };

    /// <summary>
    /// Sets the minimum width style.
    /// </summary>
    /// <param name="value">The minimum width value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithMinWidth(object value) => this with { MinWidth = value };

    /// <summary>
    /// Sets the height style.
    /// </summary>
    /// <param name="value">The height value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithHeight(object value) => this with { Height = value };

    /// <summary>
    /// Sets the flex direction style.
    /// </summary>
    /// <param name="value">The flex direction value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithFlexDirection(object value) => this with { FlexDirection = value };

    /// <summary>
    /// Sets the flex wrap style.
    /// </summary>
    /// <param name="value">The flex wrap value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithFlexWrap(object value) => this with { FlexWrap = value };

    /// <summary>
    /// Sets the flex flow style.
    /// </summary>
    /// <param name="value">The flex flow value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithFlexFlow(object value) => this with { FlexFlow = value };

    /// <summary>
    /// Sets the justify content style.
    /// </summary>
    /// <param name="value">The justify content value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithJustifyContent(object value) => this with { JustifyContent = value };

    /// <summary>
    /// Sets the align content style.
    /// </summary>
    /// <param name="value">The align content value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithAlignContent(object value) => this with { AlignContent = value };

    /// <summary>
    /// Sets the align items style.
    /// </summary>
    /// <param name="value">The align items value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithAlignItems(object value) => this with { AlignItems = value };

    /// <summary>
    /// Sets the gap style.
    /// </summary>
    /// <param name="value">The gap value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithGap(object value) => this with { Gap = value };

    /// <summary>
    /// Sets the row gap style.
    /// </summary>
    /// <param name="value">The row gap value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithRowGap(object value) => this with { RowGap = value };

    /// <summary>
    /// Sets the column gap style.
    /// </summary>
    /// <param name="value">The column gap value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithColumnGap(object value) => this with { ColumnGap = value };

    /// <summary>
    /// Sets the align self style.
    /// </summary>
    /// <param name="value">The align self value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithAlignSelf(object value) => this with { AlignSelf = value };

    public StyleBuilder WithFlexBasis(object value) => this with { FlexBasis = value };
    public StyleBuilder WithFlexGrow(object value) => this with { FlexGrow = value };
    public StyleBuilder WithFlexShrink(object value) => this with { FlexShrink = value };
    public StyleBuilder WithOrder(object value) => this with { Order = value };
    public StyleBuilder WithMargin(object value) => this with { Margin = value };
    public StyleBuilder WithPadding(object value) => this with { Padding = value };
    public object WithBorder(object border) => this with { Border = border };

    public override string ToString()
    {
        var styleBuilder = new StringBuilder();

        if (Display != null) styleBuilder.Append($"display: {Display}; ");
        if (Width != null) styleBuilder.Append($"width: {Width}; ");
        if (MinWidth != null) styleBuilder.Append($"min-width: {MinWidth}; ");
        if (Height != null) styleBuilder.Append($"height: {Height}; ");
        if (FlexDirection != null) styleBuilder.Append($"flex-direction: {FlexDirection}; ");
        if (FlexWrap != null) styleBuilder.Append($"flex-wrap: {FlexWrap}; ");
        if (FlexFlow != null) styleBuilder.Append($"flex-flow: {FlexFlow}; ");
        if (JustifyContent != null) styleBuilder.Append($"justify-content: {JustifyContent}; ");
        if (AlignContent != null) styleBuilder.Append($"align-content: {AlignContent}; ");
        if (AlignItems != null) styleBuilder.Append($"align-items: {AlignItems}; ");
        if (Gap != null) styleBuilder.Append($"gap: {Gap}; ");
        if (RowGap != null) styleBuilder.Append($"row-gap: {RowGap}; ");
        if (ColumnGap != null) styleBuilder.Append($"column-gap: {ColumnGap}; ");
        if (AlignSelf != null) styleBuilder.Append($"align-self: {AlignSelf}; ");
        if (FlexBasis != null) styleBuilder.Append($"flex-basis: {FlexBasis}; ");
        if (FlexGrow != null) styleBuilder.Append($"flex-grow: {FlexGrow}; ");
        if (FlexShrink != null) styleBuilder.Append($"flex-shrink: {FlexShrink}; ");
        if (Order != null) styleBuilder.Append($"order: {Order}; ");
        if (Margin != null) styleBuilder.Append($"margin: {Margin}; ");
        if (Padding != null) styleBuilder.Append($"padding: {Padding}; ");
        if (Border != null) styleBuilder.Append($"border: {Border}; ");

        return styleBuilder.ToString().Trim();
    }
}
