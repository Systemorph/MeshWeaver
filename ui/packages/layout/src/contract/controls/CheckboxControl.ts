import { type } from "@open-smc/serialization/src/type";
import { UiControl } from "./UiControl";
import { ValueOrBinding } from "@open-smc/data/src/contract/Binding";

@type("MeshWeaver.Layout.CheckBoxControl")
export class CheckboxControl extends UiControl {
    data: ValueOrBinding<boolean>
}