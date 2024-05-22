import type {
    MenuItemSkin,
    MenuItemView
} from "@open-smc/application/src/controls/MenuItemControl";
import { ExpandableControl } from "./ExpandableControl";
import { IconDef } from "@open-smc/ui-kit/src/components/renderIcon";

class MenuItem extends ExpandableControl implements MenuItemView {
    title: string;
    icon: IconDef;
    skin: MenuItemSkin;
    color: string;

    constructor() {
        super("MenuItemControl");
    }

    withTitle(title: string) {
        this.title = title;
        return this;
    }

    withIcon(icon: IconDef) {
        this.icon = icon;
        return this;
    }

    withColor(color: string) {
        this.color = color;
        return this;
    }

    withSkin(value: MenuItemSkin) {
        return super.withSkin(value);
    }
}

export const makeMenuItem = () => new MenuItem();