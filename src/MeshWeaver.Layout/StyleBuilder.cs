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
    public object Border { get; init; }

    // Individual margin properties
    public object MarginTop { get; init; }
    public object MarginRight { get; init; }
    public object MarginBottom { get; init; }
    public object MarginLeft { get; init; }

    // Individual padding properties
    public object PaddingTop { get; init; }
    public object PaddingRight { get; init; }
    public object PaddingBottom { get; init; }
    public object PaddingLeft { get; init; }

    // Individual border properties
    public object BorderTop { get; init; }
    public object BorderRight { get; init; }
    public object BorderBottom { get; init; }
    public object BorderLeft { get; init; }
    public object BorderWidth { get; init; }
    public object BorderStyle { get; init; }
    public object BorderColor { get; init; }
    public object BorderRadius { get; init; }

    // Additional common CSS properties
    public object Color { get; init; }
    public object BackgroundColor { get; init; }
    public object FontSize { get; init; }
    public object FontWeight { get; init; }
    public object TextAlign { get; init; }
    public object Position { get; init; }
    public object Top { get; init; }
    public object Right { get; init; }
    public object Bottom { get; init; }
    public object Left { get; init; }
    public object BoxShadow { get; init; }


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
    public StyleBuilder WithBorder(object border) => this with { Border = border };

    // Individual margin methods
    public StyleBuilder WithMarginTop(object value) => this with { MarginTop = value };
    public StyleBuilder WithMarginRight(object value) => this with { MarginRight = value };
    public StyleBuilder WithMarginBottom(object value) => this with { MarginBottom = value };
    public StyleBuilder WithMarginLeft(object value) => this with { MarginLeft = value };

    // Individual padding methods
    public StyleBuilder WithPaddingTop(object value) => this with { PaddingTop = value };
    public StyleBuilder WithPaddingRight(object value) => this with { PaddingRight = value };
    public StyleBuilder WithPaddingBottom(object value) => this with { PaddingBottom = value };
    public StyleBuilder WithPaddingLeft(object value) => this with { PaddingLeft = value };

    // Individual border methods
    public StyleBuilder WithBorderTop(object value) => this with { BorderTop = value };
    public StyleBuilder WithBorderRight(object value) => this with { BorderRight = value };
    public StyleBuilder WithBorderBottom(object value) => this with { BorderBottom = value };
    public StyleBuilder WithBorderLeft(object value) => this with { BorderLeft = value };
    public StyleBuilder WithBorderWidth(object value) => this with { BorderWidth = value };
    public StyleBuilder WithBorderStyle(object value) => this with { BorderStyle = value };
    public StyleBuilder WithBorderColor(object value) => this with { BorderColor = value };
    public StyleBuilder WithBorderRadius(object value) => this with { BorderRadius = value };

    // Additional common CSS property methods
    public StyleBuilder WithColor(object value) => this with { Color = value };
    public StyleBuilder WithBackgroundColor(object value) => this with { BackgroundColor = value };
    public StyleBuilder WithFontSize(object value) => this with { FontSize = value };
    public StyleBuilder WithFontWeight(object value) => this with { FontWeight = value };
    public StyleBuilder WithTextAlign(object value) => this with { TextAlign = value };
    public StyleBuilder WithPosition(object value) => this with { Position = value };
    public StyleBuilder WithTop(object value) => this with { Top = value };
    public StyleBuilder WithRight(object value) => this with { Right = value };
    public StyleBuilder WithBottom(object value) => this with { Bottom = value };
    public StyleBuilder WithLeft(object value) => this with { Left = value };
    public StyleBuilder WithBoxShadow(object value) => this with { BoxShadow = value };

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

        // Add individual margin properties
        if (MarginTop != null) styleBuilder.Append($"margin-top: {MarginTop}; ");
        if (MarginRight != null) styleBuilder.Append($"margin-right: {MarginRight}; ");
        if (MarginBottom != null) styleBuilder.Append($"margin-bottom: {MarginBottom}; ");
        if (MarginLeft != null) styleBuilder.Append($"margin-left: {MarginLeft}; ");

        // Add individual padding properties
        if (PaddingTop != null) styleBuilder.Append($"padding-top: {PaddingTop}; ");
        if (PaddingRight != null) styleBuilder.Append($"padding-right: {PaddingRight}; ");
        if (PaddingBottom != null) styleBuilder.Append($"padding-bottom: {PaddingBottom}; ");
        if (PaddingLeft != null) styleBuilder.Append($"padding-left: {PaddingLeft}; ");

        // Add individual border properties
        if (BorderTop != null) styleBuilder.Append($"border-top: {BorderTop}; ");
        if (BorderRight != null) styleBuilder.Append($"border-right: {BorderRight}; ");
        if (BorderBottom != null) styleBuilder.Append($"border-bottom: {BorderBottom}; ");
        if (BorderLeft != null) styleBuilder.Append($"border-left: {BorderLeft}; ");
        if (BorderWidth != null) styleBuilder.Append($"border-width: {BorderWidth}; ");
        if (BorderStyle != null) styleBuilder.Append($"border-style: {BorderStyle}; ");
        if (BorderColor != null) styleBuilder.Append($"border-color: {BorderColor}; ");
        if (BorderRadius != null) styleBuilder.Append($"border-radius: {BorderRadius}; ");

        // Add additional common CSS properties
        if (Color != null) styleBuilder.Append($"color: {Color}; ");
        if (BackgroundColor != null) styleBuilder.Append($"background-color: {BackgroundColor}; ");
        if (FontSize != null) styleBuilder.Append($"font-size: {FontSize}; ");
        if (FontWeight != null) styleBuilder.Append($"font-weight: {FontWeight}; ");
        if (TextAlign != null) styleBuilder.Append($"text-align: {TextAlign}; ");
        if (Position != null) styleBuilder.Append($"position: {Position}; ");
        if (Top != null) styleBuilder.Append($"top: {Top}; ");
        if (Right != null) styleBuilder.Append($"right: {Right}; ");
        if (Bottom != null) styleBuilder.Append($"bottom: {Bottom}; ");
        if (Left != null) styleBuilder.Append($"left: {Left}; ");
        if (BoxShadow != null) styleBuilder.Append($"box-shadow: {BoxShadow}; ");

        return styleBuilder.ToString().Trim();
    }
}
