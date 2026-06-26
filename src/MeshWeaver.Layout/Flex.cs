namespace MeshWeaver.Layout;

/// <summary>CSS display mode for a UI element.</summary>
public enum Display
{
    /// <summary>Block-level element; starts on a new line and takes full width.</summary>
    Block,
    /// <summary>Inline element; flows with surrounding content.</summary>
    Inline,
    /// <summary>Block-level flex container; enables flexbox layout.</summary>
    Flex,
    /// <summary>Inline-level flex container; enables flexbox layout without breaking the inline flow.</summary>
    InlineFlex
}

/// <summary>Restricts the display mode to flex variants only.</summary>
public enum FlexDisplay
{
    /// <summary>Block-level flex container.</summary>
    Flex,
    /// <summary>Inline-level flex container.</summary>
    InlineFlex
}

/// <summary>Controls the main axis direction of a flex container.</summary>
public enum FlexDirection
{
    /// <summary>Items are placed left to right (default).</summary>
    Row,
    /// <summary>Items are placed right to left.</summary>
    RowReverse,
    /// <summary>Items are placed top to bottom.</summary>
    Column,
    /// <summary>Items are placed bottom to top.</summary>
    ColumnReverse
}

/// <summary>Controls whether flex items wrap onto multiple lines.</summary>
public enum FlexWrap
{
    /// <summary>All items placed on a single line; no wrapping.</summary>
    Nowrap,
    /// <summary>Items wrap onto additional lines from top to bottom.</summary>
    Wrap,
    /// <summary>Items wrap onto additional lines from bottom to top.</summary>
    WrapReverse
}

// public enum FlexFlow
// {
//     // Row nowrap,
//     ColumnReverse,
//     // column wrap,
//     // row-reverse wrap-reverse
// }

/// <summary>Controls alignment of flex items along the main axis.</summary>
public enum JustifyContent
{
    /// <summary>Items are packed toward the start of the main axis.</summary>
    FlexStart,
    /// <summary>Items are packed toward the end of the main axis.</summary>
    FlexEnd,
    /// <summary>Items are centered along the main axis.</summary>
    Center,
    /// <summary>Items are evenly distributed; first item at start, last at end.</summary>
    SpaceBetween,
    /// <summary>Items are evenly distributed with equal space around each item.</summary>
    SpaceAround,
    /// <summary>Items are evenly distributed with equal space between and around them.</summary>
    SpaceEvenly
}

/// <summary>Controls alignment of flex items along the cross axis.</summary>
public enum AlignItems
{
    /// <summary>Items are aligned to the start of the cross axis.</summary>
    FlexStart,
    /// <summary>Items are aligned to the end of the cross axis.</summary>
    FlexEnd,
    /// <summary>Items are centered along the cross axis.</summary>
    Center,
    /// <summary>Items are aligned by their text baselines.</summary>
    Baseline,
    /// <summary>Items stretch to fill the cross axis (default).</summary>
    Stretch
}

/// <summary>Controls alignment of flex lines when there is extra space in the cross axis (multi-line containers only).</summary>
public enum AlignContent
{
    /// <summary>Lines are packed toward the start of the cross axis.</summary>
    FlexStart,
    /// <summary>Lines are packed toward the end of the cross axis.</summary>
    FlexEnd,
    /// <summary>Lines are centered along the cross axis.</summary>
    Center,
    /// <summary>Lines are evenly distributed; first line at start, last at end.</summary>
    SpaceBetween,
    /// <summary>Lines are evenly distributed with equal space around each line.</summary>
    SpaceAround,
    /// <summary>Lines are evenly distributed with equal space between and around them.</summary>
    SpaceEvenly,
    /// <summary>Lines stretch to fill the remaining cross-axis space.</summary>
    Stretch
}
