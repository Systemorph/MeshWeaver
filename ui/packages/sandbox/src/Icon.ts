import type {
    BorderRadius,
    IconSize,
    IconView
} from "@open-smc/application/src/controls/IconControl";
import { IconDef } from "@open-smc/ui-kit/src/components/renderIcon";
import { ControlBase, ControlBuilderBase } from "./ControlBase";

export class Icon extends ControlBase implements IconView {
    icon: IconDef;
    color: string;
    size: IconSize;
    background: boolean;
    borderRadius: BorderRadius;
    
    constructor() {
        super("IconControl");
    }
}

export class IconBuilder extends ControlBuilderBase<Icon> {
    constructor(icon: IconDef) {
        super(Icon);
        this.withIcon(icon);
    }

    withIcon(icon: IconDef) {
        this.data.icon = icon;
        return this;
    }

    withColor(color: string) {
        this.data.color = color;
        return this;
    }

    withSize(size: IconSize) {
        this.data.size = size;
        return this;
    }

    withBackground(value: boolean) {
        this.data.background = value;
        return this;
    }

    withBorderRadius(borderRadius: BorderRadius) {
        this.data.borderRadius = borderRadius;
        return this;
    }
}

export const makeIcon = (icon: IconDef) => new IconBuilder(icon);