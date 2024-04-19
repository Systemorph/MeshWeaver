import { type } from "@open-smc/serialization/src/type";
import { UiControl } from "./UiControl";
import { LayoutStackSkin } from "./LayoutStackControl";

@type("OpenSmc.Layout.ItemTemplateControl")
export class ItemTemplateControl extends UiControl {
    view: UiControl;
    data?: unknown[];
    skin?: LayoutStackSkin;
}