import { type } from "@open-smc/serialization/src/type";
import { UiControl } from "./UiControl";
import { ValueOrBinding } from "@open-smc/data/src/contract/Binding";

@type("OpenSmc.Layout.CheckBoxControl")
export class CheckboxControl extends UiControl {
    data: ValueOrBinding<boolean>
}