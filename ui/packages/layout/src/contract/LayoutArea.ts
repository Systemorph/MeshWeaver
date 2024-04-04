import { Style } from "./controls/Style";
import { UiControl } from "./controls/UiControl";
import { type } from "@open-smc/serialization/src/type";

@type("OpenSmc.Layout.LayoutArea")
export class LayoutArea {
    constructor(
        public id: string,
        public control: UiControl,
        public options?: any,
        public style?: Style
    ) {
    }
}