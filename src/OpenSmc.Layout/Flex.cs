namespace OpenSmc.Layout;

public enum Display
{
    Block,
    Inline,
    Flex,
    InlineFlex
}

public enum FlexDisplay
{
    Flex,
    InlineFlex
}

public enum FlexDirection
{
    Row,
    RowReverse,
    Column,
    ColumnReverse
}

public enum FlexWrap
{
    Nowrap,
    Wrap,
    WrapReverse
}

// public enum FlexFlow
// {
//     // Row nowrap,
//     ColumnReverse,
//     // column wrap,
//     // row-reverse wrap-reverse
// }

public enum JustifyContent
{
    FlexStart,
    FlexEnd,
    Center,
    SpaceBetween,
    SpaceAround,
    SpaceEvenly
}

public enum AlignItems
{
    FlexStart,
    FlexEnd,
    Center,
    Baseline,
    Stretch
}

public enum AlignContent
{
    FlexStart,
    FlexEnd,
    Center,
    SpaceBetween,
    SpaceAround,
    SpaceEvenly,
    Stretch
}
