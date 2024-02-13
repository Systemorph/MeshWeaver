import { Builder } from "@open-smc/utils/src/Builder";
import {
    AlignContent,
    AlignItems,
    AlignSelf, Display,
    FlexBasis,
    FlexContainer,
    FlexDirection,
    FlexElement,
    FlexFlow,
    FlexWrap,
    HtmlElement,
    JustifyContent,
    Position
} from "@open-smc/application/src/Style";

class StyleBuilder extends Builder<HtmlElement & FlexContainer & FlexElement> {
    withWidth(value: string) {
        this.data.width = value;
        return this;
    }

    withHeight(value: string) {
        this.data.height = value;
        return this;
    }

    withBorder(value: string) {
        this.data.border = value;
        return this;
    }

    withBorderRadius(value: string) {
        this.data.borderRadius = value;
        return this;
    }

    // flex container

    withDisplay(value: Display) {
        this.data.display = value;
        return this;
    }

    withFlexDirection(value: FlexDirection) {
        this.data.flexDirection = value;
        return this;
    }

    withFlexWrap(value: FlexWrap) {
        this.data.flexWrap = value;
        return this;
    }

    withFlexFlow(value: FlexFlow) {
        this.data.flexFlow = value;
        return this;
    }

    withJustifyContent(value: JustifyContent) {
        this.data.justifyContent = value;
        return this;
    }

    withAlignItems(value: AlignItems) {
        this.data.alignItems = value;
        return this;
    }

    withAlignContent(value: AlignContent) {
        this.data.alignContent = value;
        return this;
    }

    withGap(gap: string) {
        this.data.gap = gap;
        return this;
    }

    withRowGap(gap: string) {
        this.data.rowGap = gap;
        return this;
    }

    withColumnGap(gap: string) {
        this.data.columnGap = gap;
        return this;
    }

    // flex element

    withAlignSelf(value: AlignSelf) {
        this.data.alignSelf = value;
        return this;
    }

    withFlexBasis(value: FlexBasis) {
        this.data.flexBasis = value;
        return this;
    }

    withFlexGrow(value: number) {
        this.data.flexGrow = value;
        return this;
    }

    withFlexShrink(value: number) {
        this.data.flexShrink = value;
        return this;
    }

    withOrder(value: number) {
        this.data.order = value;
        return this;
    }

    withMargin(margin: string) {
        this.data.margin = margin;
        return this;
    }

    withPosition(position: Position) {
        this.data.position = position;
        return this;
    }

    withMinWidth(minWidth: string) {
        this.data.minWidth = minWidth;
        return this;
    }

    withMinHeight(minHeight: string) {
        this.data.minHeight = minHeight;
        return this;
    }
}

export const makeStyle = () => new Style();
