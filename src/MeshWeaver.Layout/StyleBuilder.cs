#nullable enable
using System.Text;

namespace MeshWeaver.Layout;

/// <summary>
/// Provides methods to build and configure styles dynamically.
/// </summary>
public record StyleBuilder
{
    /// <summary>CSS <c>display</c> property controlling the element's box type (e.g., "flex", "grid", "block").</summary>
    public object? Display { get; init; }
    /// <summary>CSS <c>width</c> property for the element's content width.</summary>
    public object? Width { get; init; }
    /// <summary>CSS <c>min-width</c> property — the minimum width the element may shrink to.</summary>
    public object? MinWidth { get; init; }
    /// <summary>CSS <c>max-width</c> property — the maximum width the element may grow to.</summary>
    public object? MaxWidth { get; init; }
    /// <summary>CSS <c>height</c> property for the element's content height.</summary>
    public object? Height { get; init; }
    /// <summary>CSS <c>min-height</c> property — the minimum height the element may shrink to.</summary>
    public object? MinHeight { get; init; }
    /// <summary>CSS <c>max-height</c> property — the maximum height the element may grow to.</summary>
    public object? MaxHeight { get; init; }
    /// <summary>CSS <c>overflow</c> property controlling how content that exceeds the box is clipped or scrolled (e.g., "auto", "hidden", "scroll").</summary>
    public object? Overflow { get; init; }
    /// <summary>CSS <c>flex-direction</c> property defining the main axis of the flex container (e.g., "row", "column").</summary>
    public object? FlexDirection { get; init; }
    /// <summary>CSS <c>flex-wrap</c> property controlling whether flex items wrap onto multiple lines.</summary>
    public object? FlexWrap { get; init; }
    /// <summary>CSS <c>flex-flow</c> shorthand for <c>flex-direction</c> and <c>flex-wrap</c>.</summary>
    public object? FlexFlow { get; init; }
    /// <summary>CSS <c>justify-content</c> property aligning flex/grid items along the main axis.</summary>
    public object? JustifyContent { get; init; }
    /// <summary>CSS <c>align-content</c> property aligning flex/grid lines when there is extra space on the cross axis.</summary>
    public object? AlignContent { get; init; }
    /// <summary>CSS <c>align-items</c> property aligning flex items along the cross axis.</summary>
    public object? AlignItems { get; init; }
    /// <summary>CSS <c>gap</c> shorthand for row and column gaps between flex/grid items.</summary>
    public object? Gap { get; init; }
    /// <summary>CSS <c>row-gap</c> property for the gap between rows in a flex or grid container.</summary>
    public object? RowGap { get; init; }
    /// <summary>CSS <c>column-gap</c> property for the gap between columns in a flex or grid container.</summary>
    public object? ColumnGap { get; init; }
    /// <summary>CSS <c>align-self</c> property overriding the container's <c>align-items</c> for this item.</summary>
    public object? AlignSelf { get; init; }
    /// <summary>CSS <c>flex-basis</c> property setting the initial main size of a flex item.</summary>
    public object? FlexBasis { get; init; }
    /// <summary>CSS <c>flex-grow</c> property specifying how much the flex item will grow relative to siblings.</summary>
    public object? FlexGrow { get; init; }
    /// <summary>CSS <c>flex-shrink</c> property specifying how much the flex item will shrink relative to siblings.</summary>
    public object? FlexShrink { get; init; }
    /// <summary>CSS <c>order</c> property controlling the display order of a flex/grid item.</summary>
    public object? Order { get; init; }
    /// <summary>CSS <c>margin</c> shorthand for all four margins around the element.</summary>
    public object? Margin { get; init; }
    /// <summary>CSS <c>padding</c> shorthand for all four padding values inside the element's border.</summary>
    public object? Padding { get; init; }
    /// <summary>CSS <c>border</c> shorthand for border width, style, and color on all sides.</summary>
    public object? Border { get; init; }

    // Individual margin properties
    /// <summary>CSS <c>margin-top</c> property for the top margin of the element.</summary>
    public object? MarginTop { get; init; }
    /// <summary>CSS <c>margin-right</c> property for the right margin of the element.</summary>
    public object? MarginRight { get; init; }
    /// <summary>CSS <c>margin-bottom</c> property for the bottom margin of the element.</summary>
    public object? MarginBottom { get; init; }
    /// <summary>CSS <c>margin-left</c> property for the left margin of the element.</summary>
    public object? MarginLeft { get; init; }

    // Individual padding properties
    /// <summary>CSS <c>padding-top</c> property for the top inner spacing of the element.</summary>
    public object? PaddingTop { get; init; }
    /// <summary>CSS <c>padding-right</c> property for the right inner spacing of the element.</summary>
    public object? PaddingRight { get; init; }
    /// <summary>CSS <c>padding-bottom</c> property for the bottom inner spacing of the element.</summary>
    public object? PaddingBottom { get; init; }
    /// <summary>CSS <c>padding-left</c> property for the left inner spacing of the element.</summary>
    public object? PaddingLeft { get; init; }

    // Individual border properties
    /// <summary>CSS <c>border-top</c> shorthand for the top border (width, style, color).</summary>
    public object? BorderTop { get; init; }
    /// <summary>CSS <c>border-right</c> shorthand for the right border (width, style, color).</summary>
    public object? BorderRight { get; init; }
    /// <summary>CSS <c>border-bottom</c> shorthand for the bottom border (width, style, color).</summary>
    public object? BorderBottom { get; init; }
    /// <summary>CSS <c>border-left</c> shorthand for the left border (width, style, color).</summary>
    public object? BorderLeft { get; init; }
    /// <summary>CSS <c>border-width</c> property controlling the thickness of all borders.</summary>
    public object? BorderWidth { get; init; }
    /// <summary>CSS <c>border-style</c> property (e.g., "solid", "dashed", "dotted").</summary>
    public object? BorderStyle { get; init; }
    /// <summary>CSS <c>border-color</c> property for the color of all borders.</summary>
    public object? BorderColor { get; init; }
    /// <summary>CSS <c>border-radius</c> property for rounding the corners of the element's border box.</summary>
    public object? BorderRadius { get; init; }

    // Additional common CSS properties
    /// <summary>CSS <c>color</c> property for the foreground (text) color.</summary>
    public object? Color { get; init; }
    /// <summary>CSS <c>background-color</c> property for the element's background fill.</summary>
    public object? BackgroundColor { get; init; }
    /// <summary>CSS <c>font-size</c> property for the size of the rendered text.</summary>
    public object? FontSize { get; init; }
    /// <summary>CSS <c>font-weight</c> property for the thickness of text characters (e.g., "bold", "400", "700").</summary>
    public object? FontWeight { get; init; }
    /// <summary>CSS <c>text-align</c> property for horizontal text alignment (e.g., "left", "center", "right").</summary>
    public object? TextAlign { get; init; }
    /// <summary>CSS <c>position</c> property controlling how the element is positioned in the document (e.g., "relative", "absolute", "fixed").</summary>
    public object? Position { get; init; }
    /// <summary>CSS <c>top</c> offset used with positioned elements.</summary>
    public object? Top { get; init; }
    /// <summary>CSS <c>right</c> offset used with positioned elements.</summary>
    public object? Right { get; init; }
    /// <summary>CSS <c>bottom</c> offset used with positioned elements.</summary>
    public object? Bottom { get; init; }
    /// <summary>CSS <c>left</c> offset used with positioned elements.</summary>
    public object? Left { get; init; }
    /// <summary>CSS <c>box-shadow</c> property adding shadow effects around the element's frame.</summary>
    public object? BoxShadow { get; init; }


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
    /// Sets the maximum width style.
    /// </summary>
    /// <param name="value">The maximum width value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithMaxWidth(object value) => this with { MaxWidth = value };

    /// <summary>
    /// Sets the height style.
    /// </summary>
    /// <param name="value">The height value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithHeight(object value) => this with { Height = value };

    /// <summary>
    /// Sets the minimum height style.
    /// </summary>
    /// <param name="value">The minimum height value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithMinHeight(object value) => this with { MinHeight = value };

    /// <summary>
    /// Sets the maximum height style.
    /// </summary>
    /// <param name="value">The maximum height value.</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithMaxHeight(object value) => this with { MaxHeight = value };

    /// <summary>
    /// Sets the overflow style.
    /// </summary>
    /// <param name="value">The overflow value (e.g., "auto", "scroll", "hidden").</param>
    /// <returns>The updated <see cref="StyleBuilder"/> instance.</returns>
    public StyleBuilder WithOverflow(object value) => this with { Overflow = value };

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

    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>flex-basis</c>.</summary>
    /// <param name="value">The flex-basis value (e.g., "auto", "50%", "200px").</param>
    /// <returns>A new <see cref="StyleBuilder"/> with FlexBasis set.</returns>
    public StyleBuilder WithFlexBasis(object value) => this with { FlexBasis = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>flex-grow</c> factor.</summary>
    /// <param name="value">The flex-grow factor (e.g., 1, 2).</param>
    /// <returns>A new <see cref="StyleBuilder"/> with FlexGrow set.</returns>
    public StyleBuilder WithFlexGrow(object value) => this with { FlexGrow = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>flex-shrink</c> factor.</summary>
    /// <param name="value">The flex-shrink factor (e.g., 0, 1).</param>
    /// <returns>A new <see cref="StyleBuilder"/> with FlexShrink set.</returns>
    public StyleBuilder WithFlexShrink(object value) => this with { FlexShrink = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>order</c>.</summary>
    /// <param name="value">The order value controlling display sequence among flex/grid siblings.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with Order set.</returns>
    public StyleBuilder WithOrder(object value) => this with { Order = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>margin</c> shorthand.</summary>
    /// <param name="value">The margin value applied to all sides (e.g., "8px", "4px 8px").</param>
    /// <returns>A new <see cref="StyleBuilder"/> with Margin set.</returns>
    public StyleBuilder WithMargin(object value) => this with { Margin = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>padding</c> shorthand.</summary>
    /// <param name="value">The padding value applied to all sides (e.g., "8px", "4px 8px").</param>
    /// <returns>A new <see cref="StyleBuilder"/> with Padding set.</returns>
    public StyleBuilder WithPadding(object value) => this with { Padding = value };
    /// <summary>Returns a copy with <paramref name="border"/> as the CSS <c>border</c> shorthand.</summary>
    /// <param name="border">The border shorthand value (e.g., "1px solid #ccc").</param>
    /// <returns>A new <see cref="StyleBuilder"/> with Border set.</returns>
    public StyleBuilder WithBorder(object border) => this with { Border = border };

    // Individual margin methods
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>margin-top</c>.</summary>
    /// <param name="value">The top margin value.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with MarginTop set.</returns>
    public StyleBuilder WithMarginTop(object value) => this with { MarginTop = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>margin-right</c>.</summary>
    /// <param name="value">The right margin value.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with MarginRight set.</returns>
    public StyleBuilder WithMarginRight(object value) => this with { MarginRight = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>margin-bottom</c>.</summary>
    /// <param name="value">The bottom margin value.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with MarginBottom set.</returns>
    public StyleBuilder WithMarginBottom(object value) => this with { MarginBottom = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>margin-left</c>.</summary>
    /// <param name="value">The left margin value.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with MarginLeft set.</returns>
    public StyleBuilder WithMarginLeft(object value) => this with { MarginLeft = value };

    // Individual padding methods
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>padding-top</c>.</summary>
    /// <param name="value">The top padding value.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with PaddingTop set.</returns>
    public StyleBuilder WithPaddingTop(object value) => this with { PaddingTop = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>padding-right</c>.</summary>
    /// <param name="value">The right padding value.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with PaddingRight set.</returns>
    public StyleBuilder WithPaddingRight(object value) => this with { PaddingRight = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>padding-bottom</c>.</summary>
    /// <param name="value">The bottom padding value.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with PaddingBottom set.</returns>
    public StyleBuilder WithPaddingBottom(object value) => this with { PaddingBottom = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>padding-left</c>.</summary>
    /// <param name="value">The left padding value.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with PaddingLeft set.</returns>
    public StyleBuilder WithPaddingLeft(object value) => this with { PaddingLeft = value };

    // Individual border methods
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>border-top</c> shorthand.</summary>
    /// <param name="value">The top border value (e.g., "1px solid red").</param>
    /// <returns>A new <see cref="StyleBuilder"/> with BorderTop set.</returns>
    public StyleBuilder WithBorderTop(object value) => this with { BorderTop = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>border-right</c> shorthand.</summary>
    /// <param name="value">The right border value.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with BorderRight set.</returns>
    public StyleBuilder WithBorderRight(object value) => this with { BorderRight = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>border-bottom</c> shorthand.</summary>
    /// <param name="value">The bottom border value.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with BorderBottom set.</returns>
    public StyleBuilder WithBorderBottom(object value) => this with { BorderBottom = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>border-left</c> shorthand.</summary>
    /// <param name="value">The left border value.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with BorderLeft set.</returns>
    public StyleBuilder WithBorderLeft(object value) => this with { BorderLeft = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>border-width</c>.</summary>
    /// <param name="value">The border thickness applied to all sides.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with BorderWidth set.</returns>
    public StyleBuilder WithBorderWidth(object value) => this with { BorderWidth = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>border-style</c>.</summary>
    /// <param name="value">The border style (e.g., "solid", "dashed", "dotted").</param>
    /// <returns>A new <see cref="StyleBuilder"/> with BorderStyle set.</returns>
    public StyleBuilder WithBorderStyle(object value) => this with { BorderStyle = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>border-color</c>.</summary>
    /// <param name="value">The border color value.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with BorderColor set.</returns>
    public StyleBuilder WithBorderColor(object value) => this with { BorderColor = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>border-radius</c>.</summary>
    /// <param name="value">The corner rounding value (e.g., "4px", "50%").</param>
    /// <returns>A new <see cref="StyleBuilder"/> with BorderRadius set.</returns>
    public StyleBuilder WithBorderRadius(object value) => this with { BorderRadius = value };

    // Additional common CSS property methods
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>color</c> (foreground text color).</summary>
    /// <param name="value">The color value (e.g., "#333", "red", "rgba(0,0,0,0.5)").</param>
    /// <returns>A new <see cref="StyleBuilder"/> with Color set.</returns>
    public StyleBuilder WithColor(object value) => this with { Color = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>background-color</c>.</summary>
    /// <param name="value">The background color value.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with BackgroundColor set.</returns>
    public StyleBuilder WithBackgroundColor(object value) => this with { BackgroundColor = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>font-size</c>.</summary>
    /// <param name="value">The font size value (e.g., "14px", "1rem").</param>
    /// <returns>A new <see cref="StyleBuilder"/> with FontSize set.</returns>
    public StyleBuilder WithFontSize(object value) => this with { FontSize = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>font-weight</c>.</summary>
    /// <param name="value">The font weight value (e.g., "bold", "400", "700").</param>
    /// <returns>A new <see cref="StyleBuilder"/> with FontWeight set.</returns>
    public StyleBuilder WithFontWeight(object value) => this with { FontWeight = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>text-align</c>.</summary>
    /// <param name="value">The text alignment value (e.g., "left", "center", "right").</param>
    /// <returns>A new <see cref="StyleBuilder"/> with TextAlign set.</returns>
    public StyleBuilder WithTextAlign(object value) => this with { TextAlign = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>position</c>.</summary>
    /// <param name="value">The position value (e.g., "relative", "absolute", "fixed").</param>
    /// <returns>A new <see cref="StyleBuilder"/> with Position set.</returns>
    public StyleBuilder WithPosition(object value) => this with { Position = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>top</c> offset.</summary>
    /// <param name="value">The top offset for a positioned element.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with Top set.</returns>
    public StyleBuilder WithTop(object value) => this with { Top = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>right</c> offset.</summary>
    /// <param name="value">The right offset for a positioned element.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with Right set.</returns>
    public StyleBuilder WithRight(object value) => this with { Right = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>bottom</c> offset.</summary>
    /// <param name="value">The bottom offset for a positioned element.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with Bottom set.</returns>
    public StyleBuilder WithBottom(object value) => this with { Bottom = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>left</c> offset.</summary>
    /// <param name="value">The left offset for a positioned element.</param>
    /// <returns>A new <see cref="StyleBuilder"/> with Left set.</returns>
    public StyleBuilder WithLeft(object value) => this with { Left = value };
    /// <summary>Returns a copy with <paramref name="value"/> as the CSS <c>box-shadow</c>.</summary>
    /// <param name="value">The box shadow value (e.g., "2px 2px 4px rgba(0,0,0,0.2)").</param>
    /// <returns>A new <see cref="StyleBuilder"/> with BoxShadow set.</returns>
    public StyleBuilder WithBoxShadow(object value) => this with { BoxShadow = value };

    /// <summary>
    /// Serializes all non-null CSS properties into an inline style string suitable for use in HTML style attributes.
    /// </summary>
    /// <returns>A CSS inline style string with each set property as a <c>name: value;</c> pair, trimmed of trailing whitespace.</returns>
    public override string ToString()
    {
        var styleBuilder = new StringBuilder();

        if (Display != null) styleBuilder.Append($"display: {Display}; ");
        if (Width != null) styleBuilder.Append($"width: {Width}; ");
        if (MinWidth != null) styleBuilder.Append($"min-width: {MinWidth}; ");
        if (MaxWidth != null) styleBuilder.Append($"max-width: {MaxWidth}; ");
        if (Height != null) styleBuilder.Append($"height: {Height}; ");
        if (MinHeight != null) styleBuilder.Append($"min-height: {MinHeight}; ");
        if (MaxHeight != null) styleBuilder.Append($"max-height: {MaxHeight}; ");
        if (Overflow != null) styleBuilder.Append($"overflow: {Overflow}; ");
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
