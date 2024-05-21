import { UiControl } from "./UiControl";
import { type } from "@open-smc/serialization/src/type";
import { ValueOrBinding } from "@open-smc/data/src/contract/Binding";

@type("OpenSmc.Layout.TextBoxControl")
export class TextBoxControl extends UiControl<TextBoxControl> {
    data: ValueOrBinding<string>;
    placeholder: string;
}