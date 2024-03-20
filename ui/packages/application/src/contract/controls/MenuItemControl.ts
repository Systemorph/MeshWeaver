import { contractMessage } from "@open-smc/utils/src/contractMessage";
import { Icon } from "./Icon";
import { ExpandableControl } from "./ExpandableControl";

@contractMessage("OpenSmc.Layout.Views.MenuItemControl")
export class MenuItemControl extends ExpandableControl {
    title?: string;
    icon?: Icon;
    skin?: MenuItemSkin;
    color?: string;
}

export type MenuItemSkin = "LargeButton" | "LargeIcon" | "Link";