import { Icon } from "./Icon";
import { ExpandableControl } from "./ExpandableControl";
import { type } from "@open-smc/serialization/src/type";

@type("MeshWeaver.Layout.Views.MenuItemControl")
export class MenuItemControl extends ExpandableControl<MenuItemControl> {
    title?: string;
    icon?: Icon;
    skin?: MenuItemSkin;
    color?: string;
}

export type MenuItemSkin = "LargeButton" | "LargeIcon" | "Link";