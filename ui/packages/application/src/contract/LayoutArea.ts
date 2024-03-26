import { contractMessage } from "@open-smc/utils/src/contractMessage";
import { Style } from "./controls/Style";
import { Control } from "./controls/Control";

@contractMessage("OpenSmc.Layout")
export class LayoutArea {
    constructor(
        public id: string,
        public control: Control,
        public options?: any,
        public style?: Style
    ) {
    }
}