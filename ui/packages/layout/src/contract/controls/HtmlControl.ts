import { type } from "@open-smc/serialization/src/type";
import { UiControl } from "./UiControl";
import { ValueOrBinding } from "@open-smc/data/src/contract/Binding";

@type("OpenSmc.Layout.HtmlControl")
export class HtmlControl extends UiControl {
    constructor(public data: ValueOrBinding<string>) {
        super();
    }
}