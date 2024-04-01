import { Icon } from "./Icon";
import { ExpandableControl } from "./ExpandableControl";
import { type } from "@open-smc/serialization/src/type";

@type("OpenSmc.Layout.Views.MenuItemControl")
export class MenuItemControl extends ExpandableControl {
    title?: string;
    icon?: Icon;
    skin?: MenuItemSkin;
    color?: string;
}

export type MenuItemSkin = "LargeButton" | "LargeIcon" | "Link";