export type Style = HtmlElement & FlexContainer & FlexElement;

export type HtmlElement = {
    display?: Display;
    width?: string;
    height?: string;
    margin?: string;
    position?: Position;
    minWidth?: string;
    minHeight?: string;
    border?: string;
    borderRadius?: string;
}

export type FlexContainer = {
    flexDirection?: FlexDirection;
    flexWrap?: FlexWrap;
    flexFlow?: FlexFlow;
    justifyContent?: JustifyContent;
    alignItems?: AlignItems;
    alignContent?: AlignContent;
    gap?: string;
    rowGap?: string;
    columnGap?: string;
    margin?: string;
}

export interface FlexElement {
    order?: number;
    alignSelf?: AlignSelf;
    flexGrow?: number;
    flexShrink?: number;
    flexBasis?: FlexBasis;
}

export type Display = "block" | "inline" | FlexDisplay;
export type FlexDisplay = "flex" | "inline-flex";
export type FlexDirection = "row" | "row-reverse" | "column" | "column-reverse";
export type FlexWrap = "nowrap" | "wrap" | "wrap-reverse";
export type FlexFlow = "row nowrap" | "column-reverse" | "column wrap" | "row-reverse wrap-reverse";
export type JustifyContent = "flex-start" | "flex-end" | "center" | "space-between" | "space-around" | "space-evenly";
export type AlignItems = "flex-start" | "flex-end" | "center" | "baseline" | "stretch";
export type AlignContent =
    "flex-start"
    | "flex-end"
    | "center"
    | "space-between"
    | "space-around"
    | "space-evenly"
    | "stretch";
export type AlignSelf = AlignItems;
export type FlexBasis = number | string | "content";
export type Position = "relative" | "absolute" | "static" | "fixed" | "sticky";
