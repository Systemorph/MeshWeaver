import type {
    MenuItemSkin,
    MenuItemView
} from "@open-smc/application/src/controls/MenuItemControl";
import { ExpandableControl, ExpandableControlBuilder } from "./ExpandableControl";
import { IconDef } from "@open-smc/ui-kit/src/components/renderIcon";

export class MenuItem extends ExpandableControl implements MenuItemView {
    title: string;
    icon: IconDef;
    skin: MenuItemSkin;
    color: string;

    constructor() {
        super("MenuItemControl");
    }
}

export class MenuItemBuilder extends ExpandableControlBuilder<MenuItem> {
    constructor() {
        super(MenuItem);
    }

    withTitle(title: string) {
        this.data.title = title;
        return this;
    }

    withIcon(icon: IconDef) {
        this.data.icon = icon;
        return this;
    }

    withColor(color: string) {
        this.data.color = color;
        return this;
    }

    withSkin(value: MenuItemSkin) {
        return super.withSkin(value);
    }
}

export const makeMenuItem = () => new MenuItemBuilder();