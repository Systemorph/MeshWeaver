import { type } from "@open-smc/serialization/src/type";
import { UiControl } from "./UiControl";
import { LayoutStackSkin } from "./LayoutStackControl";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";

@type("OpenSmc.Layout.ItemTemplateControl")
export class ItemTemplateControl extends UiControl {
    view: EntityReference<UiControl>;
    data?: unknown[];
    skin?: LayoutStackSkin;
}