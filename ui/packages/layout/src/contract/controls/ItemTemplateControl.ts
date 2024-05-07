import { type } from "@open-smc/serialization/src/type";
import { UiControl } from "./UiControl";
import { LayoutStackSkin } from "./LayoutStackControl";
import { ValueOrBinding } from "@open-smc/data/src/contract/Binding";
import { Collection } from "@open-smc/data/src/contract/Collection";

@type("OpenSmc.Layout.ItemTemplateControl")
export class ItemTemplateControl extends UiControl<ItemTemplateControl> {
    view: UiControl;
    data?: ValueOrBinding<Collection>;
    skin?: LayoutStackSkin;
}