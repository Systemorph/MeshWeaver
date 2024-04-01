import { Style } from "./controls/Style";
import { Control } from "./controls/Control";
import { type } from "@open-smc/serialization/src/type";

@type("OpenSmc.Layout.LayoutArea")
export class LayoutArea {
    constructor(
        public id: string,
        public control: Control,
        public options?: any,
        public style?: Style
    ) {
    }
}